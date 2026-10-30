using System.Net;
using LedgerMind.Models;
using LedgerMind.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace LedgerMind.Tests.Smoke;

public class StartupTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public StartupTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task All_roles_are_seeded()
    {
        using var scope = _factory.Services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();

        foreach (var role in Roles.All)
            Assert.True(await roles.RoleExistsAsync(role), $"Role {role} missing");
    }

    [Fact]
    public async Task Seeded_admin_can_sign_in_and_has_admin_role()
    {
        var client = _factory.CreateClient();

        var login = await TestAuth.LoginAsync(client, TestWebApplicationFactory.AdminEmail, TestWebApplicationFactory.AdminPassword);

        Assert.False(string.IsNullOrWhiteSpace(login.Token));
        Assert.False(string.IsNullOrWhiteSpace(login.RefreshToken));
        Assert.Contains(Roles.Admin, login.Roles);
    }

    [Fact]
    public async Task Responses_carry_security_headers()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/Health");

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("default-src 'none'; frame-ancestors 'none'",
            response.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task Authenticated_api_responses_are_not_cached()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);

        var response = await client.GetAsync("/api/Auth/me");

        await response.AssertStatusAsync(HttpStatusCode.OK);
        Assert.True(response.Headers.CacheControl?.NoStore == true);
    }

    [Fact]
    public async Task Protected_endpoint_without_token_returns_401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/Invoices");

        await response.AssertStatusAsync(HttpStatusCode.Unauthorized);
    }
}
