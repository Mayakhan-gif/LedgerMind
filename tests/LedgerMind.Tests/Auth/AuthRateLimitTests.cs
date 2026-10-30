using System.Net;
using System.Net.Http.Json;
using LedgerMind.Tests.Infrastructure;

namespace LedgerMind.Tests.Auth;

// Auth endpoints par 3 requests ki had, lamba window taake test ke dauran reset na ho.
// TestServer ki saari requests ek hi partition (IP) me ginti hoti hain.
public sealed class AuthRateLimitFactory : TestWebApplicationFactory
{
    public AuthRateLimitFactory()
    {
        Settings["RateLimiting:AuthPermitLimit"] = "3";
        Settings["RateLimiting:AuthWindowSeconds"] = "600";
    }
}

public class AuthRateLimitTests : IClassFixture<AuthRateLimitFactory>
{
    private readonly AuthRateLimitFactory _factory;

    public AuthRateLimitTests(AuthRateLimitFactory factory)
    {
        _factory = factory;
    }

    // Is class me sirf yahi test: had poori class ke liye ek hi hai
    [Fact]
    public async Task Fourth_auth_request_in_the_window_returns_429_with_a_message()
    {
        var client = _factory.CreateClient();

        // login aur register ek hi had me gine jate hain
        var login = await client.PostAsJsonAsync("/api/Auth/login",
            new { email = AuthTestHelpers.NewEmail("nobody"), password = "Wrong#Pass1" });
        await login.AssertStatusAsync(HttpStatusCode.Unauthorized);

        var register = await client.PostAsJsonAsync("/api/Auth/register", new
        {
            fullName = "Rate Limited",
            email = AuthTestHelpers.NewEmail("rate"),
            password = TestWebApplicationFactory.DefaultPassword
        });
        await register.AssertStatusAsync(HttpStatusCode.OK);

        var secondLogin = await client.PostAsJsonAsync("/api/Auth/login",
            new { email = AuthTestHelpers.NewEmail("nobody"), password = "Wrong#Pass1" });
        await secondLogin.AssertStatusAsync(HttpStatusCode.Unauthorized);

        // Chauthi request: sahi password ke bawajood 429
        var limited = await client.PostAsJsonAsync("/api/Auth/login", new
        {
            email = TestWebApplicationFactory.AdminEmail,
            password = TestWebApplicationFactory.AdminPassword
        });
        await limited.AssertStatusAsync(HttpStatusCode.TooManyRequests);
        Assert.False(string.IsNullOrWhiteSpace(await limited.ReadMessageAsync()));

        // Refresh ki apni (zyada) had hai: login band hone par bhi session refresh chalta hai
        var refresh = await client.PostAsJsonAsync("/api/Auth/refresh", new { refreshToken = "never-issued-token" });
        await refresh.AssertStatusAsync(HttpStatusCode.Unauthorized);

        // Jo endpoints kisi policy me nahi, un par had nahi lagti
        var me = await client.GetAsync("/api/Auth/me");
        await me.AssertStatusAsync(HttpStatusCode.Unauthorized);
    }
}
