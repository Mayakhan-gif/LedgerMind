using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LedgerMind.Models;
using LedgerMind.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace LedgerMind.Tests.Auth;

public class RegistrationTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public RegistrationTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Register_creates_an_employee_only_and_is_audited()
    {
        var email = AuthTestHelpers.NewEmail("self");
        var client = _factory.CreateClient();

        // Body me role bhejne se kuch nahi hota: hamesha Employee
        var response = await client.PostAsJsonAsync("/api/Auth/register", new
        {
            fullName = "Self Registered",
            email,
            password = TestWebApplicationFactory.DefaultPassword,
            role = Roles.Admin
        });

        await response.AssertStatusAsync(HttpStatusCode.OK);
        Assert.False(string.IsNullOrWhiteSpace(await response.ReadMessageAsync()));

        string userId;
        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var created = await users.FindByEmailAsync(email);
            Assert.NotNull(created);
            userId = created!.Id;
            Assert.Equal("Self Registered", created.FullName);
            Assert.Equal(Roles.Employee, Assert.Single(await users.GetRolesAsync(created)));
        }

        var row = Assert.Single(await AuthTestHelpers.AuditRowsAsync(_factory, "UserRegistered", userId));
        Assert.Equal(userId, row.UserId);
        Assert.Equal(email, row.UserEmail);

        var login = await TestAuth.LoginAsync(client, email, TestWebApplicationFactory.DefaultPassword);
        Assert.Equal(Roles.Employee, Assert.Single(login.Roles));
    }

    [Fact]
    public async Task Register_with_an_existing_email_returns_400_with_errors()
    {
        var existing = await TestAuth.CreateUserAsync(_factory, Roles.Employee);

        var response = await _factory.CreateClient().PostAsJsonAsync("/api/Auth/register", new
        {
            fullName = "Copy Cat",
            email = existing.Email,
            password = TestWebApplicationFactory.DefaultPassword
        });

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        var json = await response.ReadJsonAsync();
        Assert.Equal(JsonValueKind.Array, json.ValueKind);
        Assert.True(json.GetArrayLength() > 0);
    }

    [Fact]
    public async Task Register_with_a_weak_password_returns_400_and_creates_no_user()
    {
        var email = AuthTestHelpers.NewEmail("weak");

        // 6 characters hain lekin digit / capital letter nahi (Identity ke qaide)
        var response = await _factory.CreateClient().PostAsJsonAsync("/api/Auth/register", new
        {
            fullName = "Weak Password",
            email,
            password = "abcdef"
        });

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Equal(JsonValueKind.Array, (await response.ReadJsonAsync()).ValueKind);

        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        Assert.Null(await users.FindByEmailAsync(email));
    }

    [Fact]
    public async Task Register_with_an_invalid_body_returns_400()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync("/api/Auth/register", new
        {
            fullName = "",
            email = "not-an-email",
            password = "123"
        });

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
    }
}

// Production jaisi setting: self-registration band
public sealed class RegistrationDisabledFactory : TestWebApplicationFactory
{
    public RegistrationDisabledFactory()
    {
        Settings["Auth:AllowSelfRegistration"] = "false";
    }
}

public class RegistrationDisabledTests : IClassFixture<RegistrationDisabledFactory>
{
    private readonly RegistrationDisabledFactory _factory;

    public RegistrationDisabledTests(RegistrationDisabledFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Register_returns_403_and_creates_no_user()
    {
        var email = AuthTestHelpers.NewEmail("blocked");

        var response = await _factory.CreateClient().PostAsJsonAsync("/api/Auth/register", new
        {
            fullName = "Blocked User",
            email,
            password = TestWebApplicationFactory.DefaultPassword
        });

        await response.AssertStatusAsync(HttpStatusCode.Forbidden);
        Assert.Equal("Khud register karna band hai. Admin se account banwayein.", await response.ReadMessageAsync());

        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        Assert.Null(await users.FindByEmailAsync(email));
    }

    [Fact]
    public async Task Login_still_works_when_self_registration_is_off()
    {
        var login = await TestAuth.LoginAsync(
            _factory.CreateClient(), TestWebApplicationFactory.AdminEmail, TestWebApplicationFactory.AdminPassword);

        Assert.Contains(Roles.Admin, login.Roles);
    }
}
