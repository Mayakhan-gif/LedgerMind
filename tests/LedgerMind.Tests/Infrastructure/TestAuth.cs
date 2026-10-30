using System.Net.Http.Headers;
using System.Net.Http.Json;
using LedgerMind.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace LedgerMind.Tests.Infrastructure;

public sealed record LoginResult(string Token, string RefreshToken, string[] Roles);

public sealed record TestUser(string Id, string Email, string Password, string Role);

public static class TestAuth
{
    // Database me seedha user banata hai (API ke baghair), unique email ke saath
    public static async Task<TestUser> CreateUserAsync(
        TestWebApplicationFactory factory,
        string role,
        string? email = null,
        string password = TestWebApplicationFactory.DefaultPassword,
        string? fullName = null)
    {
        email ??= $"{role.ToLowerInvariant()}-{Guid.NewGuid():N}@ledgermind.test";

        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            FullName = fullName ?? $"Test {role}"
        };

        var created = await users.CreateAsync(user, password);
        if (!created.Succeeded)
            throw new InvalidOperationException(
                "Test user nahi bana: " + string.Join("; ", created.Errors.Select(e => e.Description)));

        var added = await users.AddToRoleAsync(user, role);
        if (!added.Succeeded)
            throw new InvalidOperationException(
                "Role nahi laga: " + string.Join("; ", added.Errors.Select(e => e.Description)));

        return new TestUser(user.Id, email, password, role);
    }

    public static async Task<LoginResult> LoginAsync(HttpClient client, string email, string password)
    {
        var response = await client.PostAsJsonAsync("/api/Auth/login", new { email, password });
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Login fail ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync()}");

        var result = await response.Content.ReadFromJsonAsync<LoginResult>();
        return result ?? throw new InvalidOperationException("Login ka jawab khali tha.");
    }

    // Login karke Bearer token wala client
    public static async Task<HttpClient> ClientForAsync(
        TestWebApplicationFactory factory, string email, string password)
    {
        var client = factory.CreateClient();
        var login = await LoginAsync(client, email, password);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
        return client;
    }

    public static Task<HttpClient> ClientForAsync(TestWebApplicationFactory factory, TestUser user) =>
        ClientForAsync(factory, user.Email, user.Password);

    public static Task<HttpClient> AdminClientAsync(TestWebApplicationFactory factory) =>
        ClientForAsync(factory, TestWebApplicationFactory.AdminEmail, TestWebApplicationFactory.AdminPassword);

    // Naya user + uska logged-in client ek saath
    public static async Task<(TestUser User, HttpClient Client)> NewUserClientAsync(
        TestWebApplicationFactory factory, string role)
    {
        var user = await CreateUserAsync(factory, role);
        var client = await ClientForAsync(factory, user);
        return (user, client);
    }

    public static async Task<string> GetAdminIdAsync(TestWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var admin = await users.FindByEmailAsync(TestWebApplicationFactory.AdminEmail);
        return admin?.Id ?? throw new InvalidOperationException("Seeded admin maujood nahi.");
    }
}
