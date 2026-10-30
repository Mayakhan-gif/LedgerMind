using System.Net;
using LedgerMind.Tests.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace LedgerMind.Tests.Xero;

// Xero:ClientSecret khali: token refresh / callback 500 { message }, baqi kaam chalta rahe
public sealed class XeroMissingSecretFactory : TestWebApplicationFactory
{
    public XeroMissingSecretFactory()
    {
        Settings["Xero:ClientSecret"] = "";
    }
}

// Xero:RedirectUri khali: connect-url aur callback dono 500 { message }
public sealed class XeroMissingRedirectUriFactory : TestWebApplicationFactory
{
    public XeroMissingRedirectUriFactory()
    {
        Settings["Xero:RedirectUri"] = "";
    }
}

public class XeroMissingSecretTests : IClassFixture<XeroMissingSecretFactory>
{
    private const string ConfigErrorMessage = "Xero ki setting mukammal nahi.";

    private readonly XeroMissingSecretFactory _factory;

    public XeroMissingSecretTests(XeroMissingSecretFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Refresh_without_client_secret_returns_500_with_message()
    {
        await XeroTestHelpers.ResetAsync(_factory);
        var connection = await XeroTestHelpers.SeedConnectionAsync(
            _factory, "ns-access", "ns-refresh", DateTime.UtcNow.AddMinutes(-5));
        var admin = await TestAuth.AdminClientAsync(_factory);

        var response = await admin.GetAsync("/api/Xero/invoices");

        await response.AssertStatusAsync(HttpStatusCode.InternalServerError);
        Assert.Equal(ConfigErrorMessage, await response.ReadMessageAsync());
        Assert.Empty(_factory.XeroHttp.Requests);

        var row = await XeroTestHelpers.FindConnectionAsync(_factory, connection.TenantId);
        Assert.NotNull(row);
        Assert.Equal("ns-refresh", XeroTestHelpers.TokenProtector(_factory).Unprotect(row!.RefreshTokenProtected));
    }

    [Fact]
    public async Task Fresh_access_token_reads_live_invoices_without_client_secret()
    {
        await XeroTestHelpers.ResetAsync(_factory);
        await XeroTestHelpers.SeedConnectionAsync(_factory, "fresh-access", "fresh-refresh", DateTime.UtcNow.AddMinutes(30));
        _factory.XeroHttp.EnqueueJson(HttpStatusCode.OK, XeroTestHelpers.InvoicesPageJson(2, "NS"));
        var admin = await TestAuth.AdminClientAsync(_factory);

        var response = await admin.GetAsync("/api/Xero/invoices");

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var json = await response.ReadJsonAsync();
        Assert.False(json.GetProperty("isDemo").GetBoolean());
        Assert.Equal(2, json.GetProperty("count").GetInt32());
        var request = Assert.Single(_factory.XeroHttp.Requests);
        Assert.Equal("Bearer fresh-access", request.Authorization);
    }

    [Fact]
    public async Task Callback_without_client_secret_returns_500_and_calls_nothing()
    {
        await XeroTestHelpers.ResetAsync(_factory);
        var adminId = await TestAuth.GetAdminIdAsync(_factory);
        var state = XeroTestHelpers.BuildState(_factory, adminId, DateTimeOffset.UtcNow.AddMinutes(5));

        var response = await _factory.CreateClient()
            .GetAsync($"/api/Xero/callback?code=c1&state={Uri.EscapeDataString(state)}");

        await response.AssertStatusAsync(HttpStatusCode.InternalServerError);
        Assert.Equal(ConfigErrorMessage, await response.ReadMessageAsync());
        Assert.Empty(_factory.XeroHttp.Requests);
        var count = await _factory.WithDbAsync(async db =>
        {
            return await db.XeroConnections.CountAsync();
        });
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Disconnect_without_client_secret_skips_revocation_but_removes_row()
    {
        await XeroTestHelpers.ResetAsync(_factory);
        var connection = await XeroTestHelpers.SeedConnectionAsync(
            _factory, "nr-access", "nr-refresh", DateTime.UtcNow.AddMinutes(30));
        var admin = await TestAuth.AdminClientAsync(_factory);

        var response = await admin.DeleteAsync("/api/Xero/connection");

        await response.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Empty(_factory.XeroHttp.Requests);
        Assert.Null(await XeroTestHelpers.FindConnectionAsync(_factory, connection.TenantId));
        Assert.Single(await XeroTestHelpers.AuditRowsAsync(_factory, "XeroDisconnected", connection.TenantId));
    }

    [Fact]
    public async Task Connect_url_does_not_need_client_secret()
    {
        await XeroTestHelpers.ResetAsync(_factory);
        var admin = await TestAuth.AdminClientAsync(_factory);

        var response = await admin.GetAsync("/api/Xero/connect-url");

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var url = (await response.ReadJsonAsync()).GetProperty("url").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(XeroTestHelpers.QueryValue(url, "state")));
    }
}

public class XeroMissingRedirectUriTests : IClassFixture<XeroMissingRedirectUriFactory>
{
    private const string ConfigErrorMessage = "Xero ki setting mukammal nahi.";

    private readonly XeroMissingRedirectUriFactory _factory;

    public XeroMissingRedirectUriTests(XeroMissingRedirectUriFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Connect_url_without_redirect_uri_returns_500_with_message()
    {
        await XeroTestHelpers.ResetAsync(_factory);
        var admin = await TestAuth.AdminClientAsync(_factory);

        var response = await admin.GetAsync("/api/Xero/connect-url");

        await response.AssertStatusAsync(HttpStatusCode.InternalServerError);
        Assert.Equal(ConfigErrorMessage, await response.ReadMessageAsync());
    }

    [Fact]
    public async Task Callback_without_redirect_uri_returns_500_and_calls_nothing()
    {
        await XeroTestHelpers.ResetAsync(_factory);
        var adminId = await TestAuth.GetAdminIdAsync(_factory);
        var state = XeroTestHelpers.BuildState(_factory, adminId, DateTimeOffset.UtcNow.AddMinutes(5));

        var response = await _factory.CreateClient()
            .GetAsync($"/api/Xero/callback?code=c1&state={Uri.EscapeDataString(state)}");

        await response.AssertStatusAsync(HttpStatusCode.InternalServerError);
        Assert.Equal(ConfigErrorMessage, await response.ReadMessageAsync());
        Assert.Empty(_factory.XeroHttp.Requests);
    }
}
