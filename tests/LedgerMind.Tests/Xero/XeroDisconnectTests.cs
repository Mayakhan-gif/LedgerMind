using System.Net;
using LedgerMind.Models;
using LedgerMind.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace LedgerMind.Tests.Xero;

// DELETE /api/Xero/connection (sirf Admin)
public class XeroDisconnectTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public XeroDisconnectTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Admin_disconnect_revokes_token_removes_row_and_audits()
    {
        await XeroTestHelpers.ResetAsync(_factory);
        var adminId = await TestAuth.GetAdminIdAsync(_factory);
        var connection = await XeroTestHelpers.SeedConnectionAsync(
            _factory, "bye-access", "bye-refresh", DateTime.UtcNow.AddMinutes(30), tenantName: "Bye Co");
        _factory.XeroHttp.EnqueueJson(HttpStatusCode.OK, "{}");
        var admin = await TestAuth.AdminClientAsync(_factory);

        var response = await admin.DeleteAsync("/api/Xero/connection");

        await response.AssertStatusAsync(HttpStatusCode.OK);
        Assert.False(string.IsNullOrWhiteSpace(await response.ReadMessageAsync()));

        // Xero par refresh token revoke ki call
        var request = Assert.Single(_factory.XeroHttp.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(XeroTestHelpers.RevocationUrl, request.Uri!.AbsoluteUri);
        Assert.Equal(XeroTestHelpers.ExpectedBasicAuth(_factory), request.Authorization);
        Assert.Contains("token=bye-refresh", request.Body);

        // Row hat gayi, audit me admin ke naam
        Assert.Null(await XeroTestHelpers.FindConnectionAsync(_factory, connection.TenantId));
        var audit = await XeroTestHelpers.AuditRowsAsync(_factory, "XeroDisconnected", connection.TenantId);
        var entry = Assert.Single(audit);
        Assert.Equal(adminId, entry.UserId);
        Assert.Equal("XeroConnection", entry.EntityType);

        // Status ab disconnected
        var status = await admin.GetAsync("/api/Xero/status");
        await status.AssertStatusAsync(HttpStatusCode.OK);
        Assert.False((await status.ReadJsonAsync()).GetProperty("connected").GetBoolean());
    }

    [Fact]
    public async Task Disconnect_removes_row_even_when_revocation_fails()
    {
        await XeroTestHelpers.ResetAsync(_factory);
        var connection = await XeroTestHelpers.SeedConnectionAsync(
            _factory, "f-access", "f-refresh", DateTime.UtcNow.AddMinutes(30));
        _factory.XeroHttp.EnqueueJson(HttpStatusCode.InternalServerError, "{\"error\":\"server\"}");
        var admin = await TestAuth.AdminClientAsync(_factory);

        var response = await admin.DeleteAsync("/api/Xero/connection");

        await response.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Single(_factory.XeroHttp.Requests);
        Assert.Null(await XeroTestHelpers.FindConnectionAsync(_factory, connection.TenantId));
        Assert.Single(await XeroTestHelpers.AuditRowsAsync(_factory, "XeroDisconnected", connection.TenantId));
    }

    [Fact]
    public async Task Disconnect_removes_row_even_when_revocation_times_out()
    {
        await XeroTestHelpers.ResetAsync(_factory);
        var connection = await XeroTestHelpers.SeedConnectionAsync(
            _factory, "slow-access", "slow-refresh", DateTime.UtcNow.AddMinutes(30));
        XeroTestHelpers.EnqueueTimeout(_factory);
        var admin = await TestAuth.AdminClientAsync(_factory);

        var response = await admin.DeleteAsync("/api/Xero/connection");

        await response.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Single(_factory.XeroHttp.Requests);
        Assert.Null(await XeroTestHelpers.FindConnectionAsync(_factory, connection.TenantId));
        Assert.Single(await XeroTestHelpers.AuditRowsAsync(_factory, "XeroDisconnected", connection.TenantId));
    }

    [Fact]
    public async Task Disconnect_after_refresh_revokes_the_rotated_refresh_token()
    {
        await XeroTestHelpers.ResetAsync(_factory);
        await XeroTestHelpers.SeedConnectionAsync(_factory, "rot-access", "rot-refresh", DateTime.UtcNow.AddMinutes(-5));
        _factory.XeroHttp.EnqueueJson(HttpStatusCode.OK, XeroTestHelpers.TokenJson("rot-new-access", "rot-new-refresh"));
        _factory.XeroHttp.EnqueueJson(HttpStatusCode.OK, XeroTestHelpers.InvoicesPageJson(1, "ROT"));
        _factory.XeroHttp.EnqueueJson(HttpStatusCode.OK, "{}");
        var admin = await TestAuth.AdminClientAsync(_factory);
        await (await admin.GetAsync("/api/Xero/invoices")).AssertStatusAsync(HttpStatusCode.OK);

        var response = await admin.DeleteAsync("/api/Xero/connection");

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var requests = _factory.XeroHttp.Requests.ToArray();
        Assert.Equal(3, requests.Length);
        Assert.Equal(XeroTestHelpers.RevocationUrl, requests[2].Uri!.AbsoluteUri);
        Assert.Contains("token=rot-new-refresh", requests[2].Body);
    }

    [Fact]
    public async Task Disconnect_removes_every_stored_connection()
    {
        await XeroTestHelpers.ResetAsync(_factory);
        var first = await XeroTestHelpers.SeedConnectionAsync(_factory, "a1", "r1", DateTime.UtcNow.AddMinutes(30));
        var second = await XeroTestHelpers.SeedConnectionAsync(_factory, "a2", "r2", DateTime.UtcNow.AddMinutes(30));
        _factory.XeroHttp.EnqueueJson(HttpStatusCode.OK, "{}");
        _factory.XeroHttp.EnqueueJson(HttpStatusCode.OK, "{}");
        var admin = await TestAuth.AdminClientAsync(_factory);

        var response = await admin.DeleteAsync("/api/Xero/connection");

        await response.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Equal(2, _factory.XeroHttp.Requests.Count);
        Assert.Null(await XeroTestHelpers.FindConnectionAsync(_factory, first.TenantId));
        Assert.Null(await XeroTestHelpers.FindConnectionAsync(_factory, second.TenantId));
    }

    [Fact]
    public async Task FinanceManager_cannot_disconnect()
    {
        await XeroTestHelpers.ResetAsync(_factory);
        var connection = await XeroTestHelpers.SeedConnectionAsync(
            _factory, "fm-access", "fm-refresh", DateTime.UtcNow.AddMinutes(30));
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);

        var response = await client.DeleteAsync("/api/Xero/connection");

        await response.AssertStatusAsync(HttpStatusCode.Forbidden);
        Assert.NotNull(await XeroTestHelpers.FindConnectionAsync(_factory, connection.TenantId));
        Assert.Empty(_factory.XeroHttp.Requests);
    }

    [Fact]
    public async Task Anonymous_cannot_disconnect()
    {
        var response = await _factory.CreateClient().DeleteAsync("/api/Xero/connection");

        await response.AssertStatusAsync(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Second_disconnect_returns_404()
    {
        await XeroTestHelpers.ResetAsync(_factory);
        await XeroTestHelpers.SeedConnectionAsync(_factory, "twice-access", "twice-refresh", DateTime.UtcNow.AddMinutes(30));
        _factory.XeroHttp.EnqueueJson(HttpStatusCode.OK, "{}");
        var admin = await TestAuth.AdminClientAsync(_factory);

        var first = await admin.DeleteAsync("/api/Xero/connection");
        var second = await admin.DeleteAsync("/api/Xero/connection");

        await first.AssertStatusAsync(HttpStatusCode.OK);
        await second.AssertStatusAsync(HttpStatusCode.NotFound);
        Assert.False(string.IsNullOrWhiteSpace(await second.ReadMessageAsync()));

        // Doosri dafa Xero ko koi call nahi
        Assert.Single(_factory.XeroHttp.Requests);
    }

    [Fact]
    public async Task After_disconnect_invoices_fall_back_to_demo()
    {
        await XeroTestHelpers.ResetAsync(_factory);
        await XeroTestHelpers.SeedConnectionAsync(_factory, "d-access", "d-refresh", DateTime.UtcNow.AddMinutes(30));
        _factory.XeroHttp.EnqueueJson(HttpStatusCode.OK, "{}");
        var admin = await TestAuth.AdminClientAsync(_factory);
        await (await admin.DeleteAsync("/api/Xero/connection")).AssertStatusAsync(HttpStatusCode.OK);

        var response = await admin.GetAsync("/api/Xero/invoices");

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var json = await response.ReadJsonAsync();
        Assert.True(json.GetProperty("isDemo").GetBoolean());
        Assert.Equal(4, json.GetProperty("count").GetInt32());
        var remaining = await _factory.WithDbAsync(async db =>
        {
            return await db.XeroConnections.CountAsync();
        });
        Assert.Equal(0, remaining);
    }
}
