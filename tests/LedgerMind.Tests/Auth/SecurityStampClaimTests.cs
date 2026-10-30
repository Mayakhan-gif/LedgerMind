using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LedgerMind.Models;
using LedgerMind.Services;
using LedgerMind.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace LedgerMind.Tests.Auth;

// Access token me security stamp ka hash, aur role badalne par naya stamp. Program.cs ka check
// (foundation) isi ko DB se mila kar purane role wala token foran rad karega.
public class SecurityStampClaimTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public SecurityStampClaimTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // JWT ka beech wala hissa (payload) base64url se JSON text
    private static string PayloadJson(string jwt)
    {
        var part = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        part = part.PadRight(part.Length + (4 - part.Length % 4) % 4, '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(part));
    }

    private static string? StampClaim(string jwt)
    {
        using var doc = JsonDocument.Parse(PayloadJson(jwt));
        return doc.RootElement.TryGetProperty(SecurityStampClaim.Type, out var value) ? value.GetString() : null;
    }

    private async Task<string> StampAsync(string userId)
    {
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByIdAsync(userId);
        Assert.NotNull(user);
        Assert.False(string.IsNullOrEmpty(user.SecurityStamp));
        return user.SecurityStamp!;
    }

    [Fact]
    public async Task Access_token_carries_a_hash_of_the_security_stamp_not_the_stamp_itself()
    {
        var user = await TestAuth.CreateUserAsync(_factory, Roles.Employee);

        var login = await TestAuth.LoginAsync(_factory.CreateClient(), user.Email, user.Password);

        var stamp = await StampAsync(user.Id);
        Assert.Equal(SecurityStampClaim.Hash(stamp), StampClaim(login.Token));
        Assert.DoesNotContain(stamp, PayloadJson(login.Token));
    }

    [Fact]
    public async Task Role_change_rotates_the_security_stamp_so_the_old_token_no_longer_matches()
    {
        var admin = await TestAuth.AdminClientAsync(_factory);
        var user = await TestAuth.CreateUserAsync(_factory, Roles.Employee);
        var oldLogin = await TestAuth.LoginAsync(_factory.CreateClient(), user.Email, user.Password);
        var oldClaim = StampClaim(oldLogin.Token);
        Assert.False(string.IsNullOrEmpty(oldClaim));

        var response = await admin.PostAsJsonAsync($"/api/Users/{user.Id}/role", new { role = Roles.FinanceManager });

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var newStampHash = SecurityStampClaim.Hash(await StampAsync(user.Id));
        Assert.NotEqual(oldClaim, newStampHash);

        // Naye login ka token naye stamp aur naye role ke saath
        var newLogin = await TestAuth.LoginAsync(_factory.CreateClient(), user.Email, user.Password);
        Assert.Equal(newStampHash, StampClaim(newLogin.Token));
        Assert.Contains(Roles.FinanceManager, newLogin.Roles);
    }

    [Fact]
    public async Task Old_access_token_is_rejected_right_after_a_role_change()
    {
        var admin = await TestAuth.AdminClientAsync(_factory);
        var user = await TestAuth.CreateUserAsync(_factory, Roles.Employee);
        var userClient = await TestAuth.ClientForAsync(_factory, user);

        var before = await userClient.GetAsync("/api/Auth/me");
        await before.AssertStatusAsync(HttpStatusCode.OK);

        var change = await admin.PostAsJsonAsync($"/api/Users/{user.Id}/role", new { role = Roles.FinanceManager });
        await change.AssertStatusAsync(HttpStatusCode.OK);

        // Token abhi expire nahi hua, magar stamp badal gaya: foran 401
        var after = await userClient.GetAsync("/api/Auth/me");
        await after.AssertStatusAsync(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Access_token_of_a_deleted_user_is_rejected()
    {
        var user = await TestAuth.CreateUserAsync(_factory, Roles.Employee);
        var userClient = await TestAuth.ClientForAsync(_factory, user);

        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var entity = await users.FindByIdAsync(user.Id);
            Assert.NotNull(entity);
            Assert.True((await users.DeleteAsync(entity!)).Succeeded);
        }

        var response = await userClient.GetAsync("/api/Auth/me");
        await response.AssertStatusAsync(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Setting_the_same_role_again_keeps_the_stamp()
    {
        var admin = await TestAuth.AdminClientAsync(_factory);
        var user = await TestAuth.CreateUserAsync(_factory, Roles.Employee);
        var before = await StampAsync(user.Id);

        var response = await admin.PostAsJsonAsync($"/api/Users/{user.Id}/role", new { role = Roles.Employee });

        await response.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Equal(before, await StampAsync(user.Id));
    }
}
