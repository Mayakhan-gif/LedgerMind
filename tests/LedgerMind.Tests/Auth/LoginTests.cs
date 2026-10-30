using System.Net;
using System.Net.Http.Json;
using LedgerMind.Models;
using LedgerMind.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace LedgerMind.Tests.Auth;

public class LoginTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public LoginTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Login_returns_token_refresh_token_and_roles()
    {
        var user = await TestAuth.CreateUserAsync(_factory, Roles.FinanceManager);
        var client = _factory.CreateClient();

        var response = await AuthTestHelpers.LoginRawAsync(client, user.Email, user.Password);

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var json = await response.ReadJsonAsync();
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("token").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("refreshToken").GetString()));
        var roles = json.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ToList();
        Assert.Equal(Roles.FinanceManager, Assert.Single(roles));
    }

    [Fact]
    public async Task Successful_login_is_audited_with_user_id_and_email()
    {
        var user = await TestAuth.CreateUserAsync(_factory, Roles.Employee);

        await TestAuth.LoginAsync(_factory.CreateClient(), user.Email, user.Password);

        var row = Assert.Single(await AuthTestHelpers.AuditRowsAsync(_factory, "LoginSucceeded", user.Id));
        Assert.Equal(user.Id, row.UserId);
        Assert.Equal(user.Email, row.UserEmail);
        Assert.Equal("User", row.EntityType);
    }

    [Fact]
    public async Task Wrong_password_returns_401_counts_the_failure_and_is_audited()
    {
        var user = await TestAuth.CreateUserAsync(_factory, Roles.Employee);
        var client = _factory.CreateClient();

        var response = await AuthTestHelpers.LoginRawAsync(client, user.Email, AuthTestHelpers.WrongPassword);

        await response.AssertStatusAsync(HttpStatusCode.Unauthorized);
        Assert.Equal(AuthTestHelpers.InvalidCredentialsMessage, await response.ReadMessageAsync());

        var stored = await AuthTestHelpers.GetUserAsync(_factory, user.Id);
        Assert.Equal(1, stored.AccessFailedCount);

        var row = Assert.Single(await AuthTestHelpers.AuditRowsAsync(_factory, "LoginFailed", user.Id));
        Assert.Equal(user.Id, row.UserId);
        Assert.Equal(user.Email, row.UserEmail);
        Assert.Empty(await AuthTestHelpers.AuditRowsAsync(_factory, "LoginSucceeded", user.Id));
    }

    [Fact]
    public async Task Unknown_email_returns_401_with_the_same_message_as_wrong_password()
    {
        var client = _factory.CreateClient();

        var response = await AuthTestHelpers.LoginRawAsync(client, AuthTestHelpers.NewEmail("nobody"), "Whatever#123");

        await response.AssertStatusAsync(HttpStatusCode.Unauthorized);
        Assert.Equal(AuthTestHelpers.InvalidCredentialsMessage, await response.ReadMessageAsync());
    }

    [Fact]
    public async Task Unknown_email_login_creates_no_user_but_is_audited_without_user_id()
    {
        var email = AuthTestHelpers.NewEmail("ghost");

        var response = await AuthTestHelpers.LoginRawAsync(_factory.CreateClient(), email, "Whatever#123");

        await response.AssertStatusAsync(HttpStatusCode.Unauthorized);

        // Timing ke liye bana dummy user sirf hashing ke kaam aata hai, save nahi hota
        var normalized = email.ToUpperInvariant();
        Assert.False(await _factory.WithDbAsync<bool>(db => db.Users.AnyAsync(u => u.NormalizedEmail == normalized)));

        // Nakam koshish ka record (S6), magar kisi user se juda nahi
        var rows = await UnknownEmailAuditRowsAsync(normalized);
        var row = Assert.Single(rows);
        Assert.Equal("LoginFailedUnknownEmail", row.Action);
        Assert.Equal("User", row.EntityType);
        Assert.Null(row.UserId);
        Assert.Null(row.EntityId);
    }

    // Anjaan email ke audit rows (normalized, yani upper-case email ke saath likhe jate hain)
    private Task<List<AuditLog>> UnknownEmailAuditRowsAsync(string normalizedEmail) =>
        _factory.WithDbAsync(db => db.AuditLogs
            .AsNoTracking()
            .Where(a => a.UserEmail == normalizedEmail)
            .ToListAsync());

    [Fact]
    public async Task Login_with_invalid_body_returns_400()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/Auth/login", new { email = "not-an-email", password = "" });

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account_even_for_the_correct_password()
    {
        var user = await TestAuth.CreateUserAsync(_factory, Roles.Employee);
        var client = _factory.CreateClient();

        // 1-4: 401, 5th: lock + 429
        await AuthTestHelpers.LockOutViaLoginAsync(client, user.Email);

        // Sahi password bhi lock ke dauran nahi chalta
        var blocked = await AuthTestHelpers.LoginRawAsync(client, user.Email, user.Password);

        await blocked.AssertStatusAsync(HttpStatusCode.TooManyRequests);
        Assert.True(blocked.Headers.TryGetValues("Retry-After", out var values));
        var seconds = int.Parse(values!.First());
        Assert.InRange(seconds, 1, AuthTestHelpers.LockoutSeconds);
        Assert.False(string.IsNullOrWhiteSpace(await blocked.ReadMessageAsync()));

        var stored = await AuthTestHelpers.GetUserAsync(_factory, user.Id);
        Assert.NotNull(stored.LockoutEnd);
        Assert.True(stored.LockoutEnd > DateTimeOffset.UtcNow);

        // Lock ke dauran koi token jari nahi hua
        Assert.Empty(await AuthTestHelpers.TokensOfAsync(_factory, user.Id));
        Assert.Empty(await AuthTestHelpers.AuditRowsAsync(_factory, "LoginSucceeded", user.Id));

        // Lock ke dauran ki koshish bhi audit me
        var whileLocked = Assert.Single(await AuthTestHelpers.AuditRowsAsync(_factory, "LoginWhileLockedOut", user.Id));
        Assert.Equal(user.Id, whileLocked.UserId);
        Assert.Equal(user.Email, whileLocked.UserEmail);
    }

    [Fact]
    public async Task Unknown_email_locks_after_five_wrong_passwords_just_like_a_real_account()
    {
        var user = await TestAuth.CreateUserAsync(_factory, Roles.Employee);
        var unknownEmail = AuthTestHelpers.NewEmail("ghost-lock");
        var client = _factory.CreateClient();

        // 1-4: 401, 5vin (lock) aur chhati (lock ke dauran): 429. Har qadam par dono ka ek hi jawab,
        // warna 429 se pata chal jata ke email ka account maujood hai.
        for (var i = 1; i <= AuthTestHelpers.MaxFailedAttempts + 1; i++)
        {
            var real = await AuthTestHelpers.LoginRawAsync(client, user.Email, AuthTestHelpers.WrongPassword);
            var unknown = await AuthTestHelpers.LoginRawAsync(client, unknownEmail, AuthTestHelpers.WrongPassword);

            var expected = i < AuthTestHelpers.MaxFailedAttempts
                ? HttpStatusCode.Unauthorized
                : HttpStatusCode.TooManyRequests;
            await real.AssertStatusAsync(expected);
            await unknown.AssertStatusAsync(expected);
            Assert.Equal(await real.ReadMessageAsync(), await unknown.ReadMessageAsync());

            if (expected == HttpStatusCode.TooManyRequests)
            {
                Assert.True(unknown.Headers.TryGetValues("Retry-After", out var values));
                Assert.InRange(int.Parse(values!.First()), 1, AuthTestHelpers.LockoutSeconds);
            }
        }

        // Anjaan email ke liye user nahi bana, magar har nakam koshish audit me (bina user id ke):
        // 4 x LoginFailedUnknownEmail, 5vin par UnknownEmailLockedOut, chhati par UnknownEmailLoginWhileLockedOut
        var normalized = unknownEmail.ToUpperInvariant();
        Assert.False(await _factory.WithDbAsync<bool>(db => db.Users.AnyAsync(u => u.NormalizedEmail == normalized)));

        var rows = await UnknownEmailAuditRowsAsync(normalized);
        Assert.Equal(AuthTestHelpers.MaxFailedAttempts - 1, rows.Count(r => r.Action == "LoginFailedUnknownEmail"));
        Assert.Single(rows, r => r.Action == "UnknownEmailLockedOut");
        Assert.Single(rows, r => r.Action == "UnknownEmailLoginWhileLockedOut");
        Assert.Equal(AuthTestHelpers.MaxFailedAttempts + 1, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.Null(r.UserId);
            Assert.Null(r.EntityId);
            Assert.Equal("User", r.EntityType);
        });
    }

    [Fact]
    public async Task Locking_attempt_itself_returns_429_with_retry_after()
    {
        var user = await TestAuth.CreateUserAsync(_factory, Roles.Employee);
        var client = _factory.CreateClient();

        for (var i = 1; i < AuthTestHelpers.MaxFailedAttempts; i++)
        {
            var failed = await AuthTestHelpers.LoginRawAsync(client, user.Email, AuthTestHelpers.WrongPassword);
            await failed.AssertStatusAsync(HttpStatusCode.Unauthorized);
        }

        var locking = await AuthTestHelpers.LoginRawAsync(client, user.Email, AuthTestHelpers.WrongPassword);

        await locking.AssertStatusAsync(HttpStatusCode.TooManyRequests);
        Assert.True(locking.Headers.TryGetValues("Retry-After", out var values));
        Assert.InRange(int.Parse(values!.First()), 1, AuthTestHelpers.LockoutSeconds);
    }

    [Fact]
    public async Task Lockout_writes_login_failed_and_account_locked_out_audit_rows()
    {
        var user = await TestAuth.CreateUserAsync(_factory, Roles.Employee);
        var client = _factory.CreateClient();

        await AuthTestHelpers.LockOutViaLoginAsync(client, user.Email);

        var failed = await AuthTestHelpers.AuditRowsAsync(_factory, "LoginFailed", user.Id);
        Assert.Equal(AuthTestHelpers.MaxFailedAttempts - 1, failed.Count);
        Assert.All(failed, row => Assert.Equal(user.Id, row.UserId));

        var locked = Assert.Single(await AuthTestHelpers.AuditRowsAsync(_factory, "AccountLockedOut", user.Id));
        Assert.Equal(user.Id, locked.UserId);
        Assert.Equal(user.Email, locked.UserEmail);
    }

    [Fact]
    public async Task Successful_login_resets_the_failed_attempt_count()
    {
        var user = await TestAuth.CreateUserAsync(_factory, Roles.Employee);
        var client = _factory.CreateClient();

        for (var i = 1; i < AuthTestHelpers.MaxFailedAttempts; i++)
        {
            var failed = await AuthTestHelpers.LoginRawAsync(client, user.Email, AuthTestHelpers.WrongPassword);
            await failed.AssertStatusAsync(HttpStatusCode.Unauthorized);
        }
        Assert.Equal(AuthTestHelpers.MaxFailedAttempts - 1, (await AuthTestHelpers.GetUserAsync(_factory, user.Id)).AccessFailedCount);

        var success = await AuthTestHelpers.LoginRawAsync(client, user.Email, user.Password);
        await success.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Equal(0, (await AuthTestHelpers.GetUserAsync(_factory, user.Id)).AccessFailedCount);

        // Ginti dobara shuru: phir 4 ghalat koshishen lock nahi karti
        for (var i = 1; i < AuthTestHelpers.MaxFailedAttempts; i++)
        {
            var failed = await AuthTestHelpers.LoginRawAsync(client, user.Email, AuthTestHelpers.WrongPassword);
            await failed.AssertStatusAsync(HttpStatusCode.Unauthorized);
        }

        var again = await AuthTestHelpers.LoginRawAsync(client, user.Email, user.Password);
        await again.AssertStatusAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Me_returns_identity_and_roles_from_the_token()
    {
        var (user, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);

        var response = await client.GetAsync("/api/Auth/me");

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var json = await response.ReadJsonAsync();
        Assert.Equal(user.Id, json.GetProperty("id").GetString());
        Assert.Equal(user.Email, json.GetProperty("email").GetString());
        Assert.Equal("Test FinanceManager", json.GetProperty("fullName").GetString());
        var roles = json.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ToList();
        Assert.Equal(Roles.FinanceManager, Assert.Single(roles));
    }

    [Fact]
    public async Task Me_without_token_returns_401()
    {
        var response = await _factory.CreateClient().GetAsync("/api/Auth/me");

        await response.AssertStatusAsync(HttpStatusCode.Unauthorized);
    }
}
