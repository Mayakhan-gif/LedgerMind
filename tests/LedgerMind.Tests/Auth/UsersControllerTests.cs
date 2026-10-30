using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LedgerMind.Models;
using LedgerMind.Tests.Infrastructure;

namespace LedgerMind.Tests.Auth;

public class UsersControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public UsersControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static object NewUserBody(string role, string? email = null) => new
    {
        fullName = "Created By Admin",
        email = email ?? AuthTestHelpers.NewEmail("created"),
        password = TestWebApplicationFactory.DefaultPassword,
        role
    };

    private static JsonElement FindUser(JsonElement list, string userId) =>
        list.EnumerateArray().Single(e => e.GetProperty("id").GetString() == userId);

    private static List<string?> RolesOf(JsonElement user) =>
        user.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ToList();

    [Fact]
    public async Task Anonymous_request_returns_401()
    {
        var response = await _factory.CreateClient().GetAsync("/api/Users");

        await response.AssertStatusAsync(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData(Roles.Employee)]
    [InlineData(Roles.FinanceManager)]
    public async Task Non_admins_get_403_on_every_users_endpoint(string role)
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, role);
        var target = await TestAuth.CreateUserAsync(_factory, Roles.Employee);

        var responses = new[]
        {
            await client.GetAsync("/api/Users"),
            await client.GetAsync($"/api/Users/{target.Id}"),
            await client.PostAsJsonAsync("/api/Users", NewUserBody(Roles.Admin)),
            await client.PostAsJsonAsync($"/api/Users/{target.Id}/role", new { role = Roles.Admin }),
            await client.PostAsync($"/api/Users/{target.Id}/unlock", null)
        };

        foreach (var response in responses)
            await response.AssertStatusAsync(HttpStatusCode.Forbidden);

        // Kuch nahi badla
        Assert.Equal(Roles.Employee, Assert.Single(await AuthTestHelpers.RolesOfAsync(_factory, target.Id)));
    }

    [Fact]
    public async Task Admin_lists_users_with_roles_and_lock_state()
    {
        var user = await TestAuth.CreateUserAsync(_factory, Roles.FinanceManager);
        var admin = await TestAuth.AdminClientAsync(_factory);

        var response = await admin.GetAsync("/api/Users");

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var entry = FindUser(await response.ReadJsonAsync(), user.Id);
        Assert.Equal(user.Email, entry.GetProperty("email").GetString());
        Assert.Equal("Test FinanceManager", entry.GetProperty("fullName").GetString());
        Assert.Equal(Roles.FinanceManager, Assert.Single(RolesOf(entry)));
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("lockedOutUntil").ValueKind);
    }

    [Fact]
    public async Task Admin_gets_a_single_user_and_404_for_unknown_id()
    {
        var user = await TestAuth.CreateUserAsync(_factory, Roles.Employee);
        var admin = await TestAuth.AdminClientAsync(_factory);

        var found = await admin.GetAsync($"/api/Users/{user.Id}");
        await found.AssertStatusAsync(HttpStatusCode.OK);
        var json = await found.ReadJsonAsync();
        Assert.Equal(user.Email, json.GetProperty("email").GetString());
        Assert.Equal(Roles.Employee, Assert.Single(RolesOf(json)));

        var missing = await admin.GetAsync($"/api/Users/{Guid.NewGuid()}");
        await missing.AssertStatusAsync(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Admin_creates_a_finance_manager_who_can_log_in_with_that_role()
    {
        var admin = await TestAuth.AdminClientAsync(_factory);
        var adminId = await TestAuth.GetAdminIdAsync(_factory);
        var email = AuthTestHelpers.NewEmail("manager");

        var response = await admin.PostAsJsonAsync("/api/Users", NewUserBody(Roles.FinanceManager, email));

        await response.AssertStatusAsync(HttpStatusCode.Created);
        var json = await response.ReadJsonAsync();
        var id = json.GetProperty("id").GetString();
        Assert.False(string.IsNullOrEmpty(id));
        Assert.Equal(email, json.GetProperty("email").GetString());
        Assert.Equal("Created By Admin", json.GetProperty("fullName").GetString());
        Assert.Equal(Roles.FinanceManager, Assert.Single(RolesOf(json)));
        Assert.EndsWith(id!, response.Headers.Location?.ToString());

        var login = await TestAuth.LoginAsync(_factory.CreateClient(), email, TestWebApplicationFactory.DefaultPassword);
        Assert.Equal(Roles.FinanceManager, Assert.Single(login.Roles));

        var row = Assert.Single(await AuthTestHelpers.AuditRowsAsync(_factory, "UserCreated", id!));
        Assert.Equal(adminId, row.UserId);
    }

    [Fact]
    public async Task Create_with_an_invalid_role_returns_400_and_creates_no_user()
    {
        var admin = await TestAuth.AdminClientAsync(_factory);
        var email = AuthTestHelpers.NewEmail("badrole");

        var response = await admin.PostAsJsonAsync("/api/Users", NewUserBody("SuperUser", email));

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Equal("Role ghalat hai.", await response.ReadMessageAsync());

        var list = await (await admin.GetAsync("/api/Users")).ReadJsonAsync();
        var emails = list.EnumerateArray().Select(e => e.GetProperty("email").GetString()).ToList();
        Assert.DoesNotContain(email, emails);
    }

    [Fact]
    public async Task Create_with_a_duplicate_email_returns_400_with_errors()
    {
        var existing = await TestAuth.CreateUserAsync(_factory, Roles.Employee);
        var admin = await TestAuth.AdminClientAsync(_factory);

        var response = await admin.PostAsJsonAsync("/api/Users", NewUserBody(Roles.Employee, existing.Email));

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        var json = await response.ReadJsonAsync();
        Assert.Equal(JsonValueKind.Array, json.ValueKind);
        Assert.True(json.GetArrayLength() > 0);
    }

    [Fact]
    public async Task Create_with_an_invalid_body_returns_400()
    {
        var admin = await TestAuth.AdminClientAsync(_factory);

        var response = await admin.PostAsJsonAsync("/api/Users", new
        {
            fullName = "",
            email = "not-an-email",
            password = "123",
            role = ""
        });

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Set_role_changes_the_role_revokes_refresh_tokens_and_is_audited()
    {
        var user = await TestAuth.CreateUserAsync(_factory, Roles.Employee);
        var userClient = _factory.CreateClient();
        var login = await TestAuth.LoginAsync(userClient, user.Email, user.Password);
        var admin = await TestAuth.AdminClientAsync(_factory);
        var adminId = await TestAuth.GetAdminIdAsync(_factory);

        var response = await admin.PostAsJsonAsync($"/api/Users/{user.Id}/role", new { role = Roles.FinanceManager });

        await response.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Equal(Roles.FinanceManager, Assert.Single(await AuthTestHelpers.RolesOfAsync(_factory, user.Id)));

        // Purana session khatam: refresh token band
        var tokens = await AuthTestHelpers.TokensOfAsync(_factory, user.Id);
        Assert.NotEmpty(tokens);
        Assert.All(tokens, t => Assert.NotNull(t.RevokedAtUtc));
        var refresh = await AuthTestHelpers.RefreshAsync(userClient, login.RefreshToken);
        await refresh.AssertStatusAsync(HttpStatusCode.Unauthorized);

        // Dobara login par naya role token me
        var relogin = await TestAuth.LoginAsync(_factory.CreateClient(), user.Email, user.Password);
        Assert.Equal(Roles.FinanceManager, Assert.Single(relogin.Roles));

        var row = Assert.Single(await AuthTestHelpers.AuditRowsAsync(_factory, "RoleChanged", user.Id));
        Assert.Equal(adminId, row.UserId);
        Assert.Contains(Roles.FinanceManager, row.Details);
    }

    [Fact]
    public async Task Setting_the_same_role_changes_nothing()
    {
        var user = await TestAuth.CreateUserAsync(_factory, Roles.Employee);
        await TestAuth.LoginAsync(_factory.CreateClient(), user.Email, user.Password);
        var admin = await TestAuth.AdminClientAsync(_factory);

        var response = await admin.PostAsJsonAsync($"/api/Users/{user.Id}/role", new { role = Roles.Employee });

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var token = Assert.Single(await AuthTestHelpers.TokensOfAsync(_factory, user.Id));
        Assert.Null(token.RevokedAtUtc);
        Assert.Empty(await AuthTestHelpers.AuditRowsAsync(_factory, "RoleChanged", user.Id));
    }

    [Fact]
    public async Task Set_role_with_an_invalid_role_returns_400()
    {
        var user = await TestAuth.CreateUserAsync(_factory, Roles.Employee);
        var admin = await TestAuth.AdminClientAsync(_factory);

        var response = await admin.PostAsJsonAsync($"/api/Users/{user.Id}/role", new { role = "admin" });

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Equal("Role ghalat hai.", await response.ReadMessageAsync());
        Assert.Equal(Roles.Employee, Assert.Single(await AuthTestHelpers.RolesOfAsync(_factory, user.Id)));
    }

    [Fact]
    public async Task Set_role_with_missing_role_returns_400()
    {
        var user = await TestAuth.CreateUserAsync(_factory, Roles.Employee);
        var admin = await TestAuth.AdminClientAsync(_factory);

        var response = await admin.PostAsJsonAsync($"/api/Users/{user.Id}/role", new { });

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Set_role_for_an_unknown_user_returns_404()
    {
        var admin = await TestAuth.AdminClientAsync(_factory);

        var response = await admin.PostAsJsonAsync($"/api/Users/{Guid.NewGuid()}/role", new { role = Roles.Employee });

        await response.AssertStatusAsync(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_admin_can_be_demoted_while_another_admin_exists()
    {
        var secondAdmin = await TestAuth.CreateUserAsync(_factory, Roles.Admin);
        var admin = await TestAuth.AdminClientAsync(_factory);

        var response = await admin.PostAsJsonAsync($"/api/Users/{secondAdmin.Id}/role", new { role = Roles.Employee });

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var login = await TestAuth.LoginAsync(_factory.CreateClient(), secondAdmin.Email, secondAdmin.Password);
        Assert.Equal(Roles.Employee, Assert.Single(login.Roles));
    }

    [Fact]
    public async Task Promoted_user_gets_admin_access_after_signing_in_again()
    {
        var user = await TestAuth.CreateUserAsync(_factory, Roles.Employee);
        var admin = await TestAuth.AdminClientAsync(_factory);

        var response = await admin.PostAsJsonAsync($"/api/Users/{user.Id}/role", new { role = Roles.Admin });
        await response.AssertStatusAsync(HttpStatusCode.OK);

        // Roles JWT me hain: naya login naya role laata hai
        var promoted = await TestAuth.ClientForAsync(_factory, user);
        var list = await promoted.GetAsync("/api/Users");
        await list.AssertStatusAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Unlock_clears_the_lockout_and_is_audited()
    {
        var user = await TestAuth.CreateUserAsync(_factory, Roles.Employee);
        var anonymous = _factory.CreateClient();
        await AuthTestHelpers.LockOutViaLoginAsync(anonymous, user.Email);

        var blocked = await AuthTestHelpers.LoginRawAsync(anonymous, user.Email, user.Password);
        await blocked.AssertStatusAsync(HttpStatusCode.TooManyRequests);

        var admin = await TestAuth.AdminClientAsync(_factory);
        var adminId = await TestAuth.GetAdminIdAsync(_factory);

        var before = FindUser(await (await admin.GetAsync("/api/Users")).ReadJsonAsync(), user.Id);
        Assert.Equal(JsonValueKind.String, before.GetProperty("lockedOutUntil").ValueKind);

        var response = await admin.PostAsync($"/api/Users/{user.Id}/unlock", null);

        await response.AssertStatusAsync(HttpStatusCode.OK);

        var after = FindUser(await (await admin.GetAsync("/api/Users")).ReadJsonAsync(), user.Id);
        Assert.Equal(JsonValueKind.Null, after.GetProperty("lockedOutUntil").ValueKind);

        var stored = await AuthTestHelpers.GetUserAsync(_factory, user.Id);
        Assert.Null(stored.LockoutEnd);
        Assert.Equal(0, stored.AccessFailedCount);

        var login = await AuthTestHelpers.LoginRawAsync(anonymous, user.Email, user.Password);
        await login.AssertStatusAsync(HttpStatusCode.OK);

        var row = Assert.Single(await AuthTestHelpers.AuditRowsAsync(_factory, "UserUnlocked", user.Id));
        Assert.Equal(adminId, row.UserId);
    }

    [Fact]
    public async Task Unlock_for_an_unknown_user_returns_404()
    {
        var admin = await TestAuth.AdminClientAsync(_factory);

        var response = await admin.PostAsync($"/api/Users/{Guid.NewGuid()}/unlock", null);

        await response.AssertStatusAsync(HttpStatusCode.NotFound);
    }
}
