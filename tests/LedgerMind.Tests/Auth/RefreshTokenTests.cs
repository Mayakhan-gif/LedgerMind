using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using LedgerMind.Models;
using LedgerMind.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LedgerMind.Tests.Auth;

public class RefreshTokenTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public RefreshTokenTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private async Task<(TestUser User, HttpClient Client, LoginResult Login)> LoginNewUserAsync(
        string role = Roles.Employee)
    {
        var user = await TestAuth.CreateUserAsync(_factory, role);
        var client = _factory.CreateClient();
        var login = await TestAuth.LoginAsync(client, user.Email, user.Password);
        return (user, client, login);
    }

    private static async Task<LoginResult> ReadTokensAsync(HttpResponseMessage response)
    {
        await response.AssertStatusAsync(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<LoginResult>();
        Assert.NotNull(result);
        return result!;
    }

    [Fact]
    public async Task Refresh_rotates_the_token_and_the_new_tokens_work()
    {
        var (user, client, login) = await LoginNewUserAsync();

        var rotated = await ReadTokensAsync(await AuthTestHelpers.RefreshAsync(client, login.RefreshToken));

        Assert.False(string.IsNullOrWhiteSpace(rotated.Token));
        Assert.NotEqual(login.RefreshToken, rotated.RefreshToken);
        Assert.Equal(Roles.Employee, Assert.Single(rotated.Roles));

        // Naya access token chalta hai
        using var me = new HttpRequestMessage(HttpMethod.Get, "/api/Auth/me");
        me.Headers.Authorization = new AuthenticationHeaderValue("Bearer", rotated.Token);
        var meResponse = await client.SendAsync(me);
        await meResponse.AssertStatusAsync(HttpStatusCode.OK);

        // Naya refresh token bhi chalta hai
        var next = await AuthTestHelpers.RefreshAsync(client, rotated.RefreshToken);
        await next.AssertStatusAsync(HttpStatusCode.OK);

        // Pehla token purana: rad, aur zanjeer DB me juri hui
        var tokens = await AuthTestHelpers.TokensOfAsync(_factory, user.Id);
        Assert.Equal(3, tokens.Count);
        Assert.Single(tokens, t => t.RevokedAtUtc == null);
        Assert.Equal(2, tokens.Count(t => t.RevokedAtUtc != null && t.ReplacedByTokenHash != null));

        // Har kamyab refresh audit me (anonymous request, phir bhi user id ke saath)
        var refreshed = await AuthTestHelpers.AuditRowsAsync(_factory, "TokenRefreshed", user.Id);
        Assert.Equal(2, refreshed.Count);
        Assert.All(refreshed, row =>
        {
            Assert.Equal(user.Id, row.UserId);
            Assert.Equal(user.Email, row.UserEmail);
        });
    }

    [Fact]
    public async Task Old_token_is_rejected_after_rotation()
    {
        var (_, client, login) = await LoginNewUserAsync();

        await ReadTokensAsync(await AuthTestHelpers.RefreshAsync(client, login.RefreshToken));
        var old = await AuthTestHelpers.RefreshAsync(client, login.RefreshToken);

        await old.AssertStatusAsync(HttpStatusCode.Unauthorized);
        Assert.False(string.IsNullOrWhiteSpace(await old.ReadMessageAsync()));
    }

    [Fact]
    public async Task Reusing_a_rotated_token_revokes_every_session_and_is_audited()
    {
        var (user, client, login) = await LoginNewUserAsync();
        // Doosra device / session
        var otherSession = await TestAuth.LoginAsync(_factory.CreateClient(), user.Email, user.Password);

        var rotated = await ReadTokensAsync(await AuthTestHelpers.RefreshAsync(client, login.RefreshToken));

        // Chor (ya purana tab) wahi rotate ho chuka token dobara bhejta hai
        var reuse = await AuthTestHelpers.RefreshAsync(client, login.RefreshToken);
        await reuse.AssertStatusAsync(HttpStatusCode.Unauthorized);

        // Ab sab se naya token aur doosra session bhi band
        var newest = await AuthTestHelpers.RefreshAsync(client, rotated.RefreshToken);
        await newest.AssertStatusAsync(HttpStatusCode.Unauthorized);
        var other = await AuthTestHelpers.RefreshAsync(client, otherSession.RefreshToken);
        await other.AssertStatusAsync(HttpStatusCode.Unauthorized);

        var tokens = await AuthTestHelpers.TokensOfAsync(_factory, user.Id);
        Assert.Equal(3, tokens.Count);
        Assert.All(tokens, t => Assert.NotNull(t.RevokedAtUtc));

        var row = Assert.Single(await AuthTestHelpers.AuditRowsAsync(_factory, "RefreshTokenReuseDetected", user.Id));
        Assert.Equal(user.Id, row.UserId);
    }

    [Fact]
    public async Task Expired_refresh_token_returns_401()
    {
        var (user, client, login) = await LoginNewUserAsync();

        await _factory.WithDbAsync(async db =>
        {
            var tokens = await db.RefreshTokens.Where(t => t.UserId == user.Id).ToListAsync();
            foreach (var token in tokens)
                token.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        });

        var response = await AuthTestHelpers.RefreshAsync(client, login.RefreshToken);

        await response.AssertStatusAsync(HttpStatusCode.Unauthorized);
        // Expire hua token reuse nahi, is liye koi naya token ya alarm nahi
        Assert.Single(await AuthTestHelpers.TokensOfAsync(_factory, user.Id));
        Assert.Empty(await AuthTestHelpers.AuditRowsAsync(_factory, "RefreshTokenReuseDetected", user.Id));

        // Lekin nakami audit me
        var failed = Assert.Single(await AuthTestHelpers.AuditRowsAsync(_factory, "RefreshFailed", user.Id));
        Assert.Equal(user.Id, failed.UserId);
        Assert.Empty(await AuthTestHelpers.AuditRowsAsync(_factory, "TokenRefreshed", user.Id));
    }

    [Fact]
    public async Task Unknown_refresh_token_returns_401()
    {
        var response = await AuthTestHelpers.RefreshAsync(_factory.CreateClient(), "this-token-was-never-issued");

        await response.AssertStatusAsync(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Refresh_with_empty_body_returns_400()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync("/api/Auth/refresh", new { });

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Refresh_for_a_locked_out_user_returns_401_and_does_not_rotate()
    {
        var (user, client, login) = await LoginNewUserAsync();

        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var entity = await users.FindByIdAsync(user.Id);
            Assert.NotNull(entity);
            var locked = await users.SetLockoutEndDateAsync(entity!, DateTimeOffset.UtcNow.AddMinutes(10));
            Assert.True(locked.Succeeded);
        }

        var response = await AuthTestHelpers.RefreshAsync(client, login.RefreshToken);

        await response.AssertStatusAsync(HttpStatusCode.Unauthorized);
        var token = Assert.Single(await AuthTestHelpers.TokensOfAsync(_factory, user.Id));
        Assert.Null(token.RevokedAtUtc);

        var failed = Assert.Single(await AuthTestHelpers.AuditRowsAsync(_factory, "RefreshFailed", user.Id));
        Assert.Equal(user.Id, failed.UserId);
        Assert.Equal(user.Email, failed.UserEmail);
    }

    [Fact]
    public async Task Logout_revokes_the_refresh_token_and_is_audited()
    {
        var (user, client, login) = await LoginNewUserAsync();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);

        var response = await client.PostAsJsonAsync("/api/Auth/logout", new { refreshToken = login.RefreshToken });

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var token = Assert.Single(await AuthTestHelpers.TokensOfAsync(_factory, user.Id));
        Assert.NotNull(token.RevokedAtUtc);

        var refresh = await AuthTestHelpers.RefreshAsync(client, login.RefreshToken);
        await refresh.AssertStatusAsync(HttpStatusCode.Unauthorized);

        var row = Assert.Single(await AuthTestHelpers.AuditRowsAsync(_factory, "Logout", user.Id));
        Assert.Equal(user.Id, row.UserId);

        // Logout ke baad band token se refresh ki koshish bhi audit me
        Assert.Single(await AuthTestHelpers.AuditRowsAsync(_factory, "RefreshFailed", user.Id));

        // Logout ke baad purana token aana chori ka ishara nahi
        Assert.Empty(await AuthTestHelpers.AuditRowsAsync(_factory, "RefreshTokenReuseDetected", user.Id));
    }

    [Fact]
    public async Task Logout_with_a_token_someone_else_rotated_revokes_every_session()
    {
        var (user, client, login) = await LoginNewUserAsync();

        // Chor ne user ka refresh token pehle hi rotate kar liya
        var stolen = await ReadTokensAsync(
            await AuthTestHelpers.RefreshAsync(_factory.CreateClient(), login.RefreshToken));

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
        var response = await client.PostAsJsonAsync("/api/Auth/logout", new { refreshToken = login.RefreshToken });

        await response.AssertStatusAsync(HttpStatusCode.OK);

        // Chor wala naya token bhi band
        var thief = await AuthTestHelpers.RefreshAsync(_factory.CreateClient(), stolen.RefreshToken);
        await thief.AssertStatusAsync(HttpStatusCode.Unauthorized);
        Assert.All(await AuthTestHelpers.TokensOfAsync(_factory, user.Id), t => Assert.NotNull(t.RevokedAtUtc));

        Assert.Single(await AuthTestHelpers.AuditRowsAsync(_factory, "Logout", user.Id));
        Assert.Single(await AuthTestHelpers.AuditRowsAsync(_factory, "RefreshTokenReuseDetected", user.Id));
    }

    [Fact]
    public async Task Logout_without_access_token_returns_401()
    {
        var (_, client, login) = await LoginNewUserAsync();

        var response = await client.PostAsJsonAsync("/api/Auth/logout", new { refreshToken = login.RefreshToken });

        await response.AssertStatusAsync(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Logout_cannot_revoke_another_users_refresh_token()
    {
        var (victim, victimClient, victimLogin) = await LoginNewUserAsync();
        var (_, attackerClient) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);

        var response = await attackerClient.PostAsJsonAsync("/api/Auth/logout", new { refreshToken = victimLogin.RefreshToken });

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var token = Assert.Single(await AuthTestHelpers.TokensOfAsync(_factory, victim.Id));
        Assert.Null(token.RevokedAtUtc);

        var refresh = await AuthTestHelpers.RefreshAsync(victimClient, victimLogin.RefreshToken);
        await refresh.AssertStatusAsync(HttpStatusCode.OK);
    }
}
