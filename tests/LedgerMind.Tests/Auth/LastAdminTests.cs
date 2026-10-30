using System.Net;
using System.Net.Http.Json;
using LedgerMind.Models;
using LedgerMind.Tests.Infrastructure;

namespace LedgerMind.Tests.Auth;

// Apna alag database jisme seeded admin hi akela Admin hai.
// Is class ka koi test naya Admin nahi banata.
public sealed class SingleAdminFactory : TestWebApplicationFactory
{
}

public class LastAdminTests : IClassFixture<SingleAdminFactory>
{
    private readonly SingleAdminFactory _factory;

    public LastAdminTests(SingleAdminFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData(Roles.FinanceManager)]
    [InlineData(Roles.Employee)]
    public async Task The_last_admin_cannot_be_demoted(string newRole)
    {
        var adminId = await TestAuth.GetAdminIdAsync(_factory);
        var admin = await TestAuth.AdminClientAsync(_factory);

        var response = await admin.PostAsJsonAsync($"/api/Users/{adminId}/role", new { role = newRole });

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Equal("Kam az kam ek Admin hona zaroori hai.", await response.ReadMessageAsync());
        Assert.Equal(Roles.Admin, Assert.Single(await AuthTestHelpers.RolesOfAsync(_factory, adminId)));
        Assert.Empty(await AuthTestHelpers.AuditRowsAsync(_factory, "RoleChanged", adminId));

        // Admin ke tokens bhi band nahi hue
        Assert.Contains(await AuthTestHelpers.TokensOfAsync(_factory, adminId), t => t.RevokedAtUtc == null);
    }

    [Fact]
    public async Task Setting_admin_on_the_last_admin_is_a_no_op()
    {
        var adminId = await TestAuth.GetAdminIdAsync(_factory);
        var admin = await TestAuth.AdminClientAsync(_factory);

        var response = await admin.PostAsJsonAsync($"/api/Users/{adminId}/role", new { role = Roles.Admin });

        await response.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Equal(Roles.Admin, Assert.Single(await AuthTestHelpers.RolesOfAsync(_factory, adminId)));
    }
}
