using System.Net;
using LedgerMind.Models;
using LedgerMind.Tests.Infrastructure;

namespace LedgerMind.Tests.Xero;

// Xero:UseDemoData = true: connection ho tab bhi demo invoices
public sealed class XeroDemoDataFactory : TestWebApplicationFactory
{
    public XeroDemoDataFactory()
    {
        Settings["Xero:UseDemoData"] = "true";
    }
}

public class XeroDemoModeTests : IClassFixture<XeroDemoDataFactory>
{
    private readonly XeroDemoDataFactory _factory;

    public XeroDemoModeTests(XeroDemoDataFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Demo_setting_returns_demo_invoices_even_when_connected()
    {
        await XeroTestHelpers.ResetAsync(_factory);
        await XeroTestHelpers.SeedConnectionAsync(_factory, "demo-access", "demo-refresh", DateTime.UtcNow.AddMinutes(30));
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);

        var response = await client.GetAsync("/api/Xero/invoices");

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var json = await response.ReadJsonAsync();
        Assert.True(json.GetProperty("isDemo").GetBoolean());
        Assert.Equal(4, json.GetProperty("count").GetInt32());
        Assert.Empty(_factory.XeroHttp.Requests);
    }

    [Fact]
    public async Task Demo_setting_is_reported_in_status_while_connected()
    {
        await XeroTestHelpers.ResetAsync(_factory);
        await XeroTestHelpers.SeedConnectionAsync(
            _factory, "demo-access", "demo-refresh", DateTime.UtcNow.AddMinutes(30), tenantName: "Demo Mode Co");
        var admin = await TestAuth.AdminClientAsync(_factory);

        var response = await admin.GetAsync("/api/Xero/status");

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var json = await response.ReadJsonAsync();
        Assert.True(json.GetProperty("connected").GetBoolean());
        Assert.False(json.GetProperty("usingLiveData").GetBoolean());
        Assert.Equal("Demo Mode Co", json.GetProperty("tenantName").GetString());
    }
}
