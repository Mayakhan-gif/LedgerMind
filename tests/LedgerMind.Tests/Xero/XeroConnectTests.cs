using System.Net;
using System.Net.Http.Json;
using LedgerMind.Models;
using LedgerMind.Tests.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace LedgerMind.Tests.Xero;

// connect-url, callback (OAuth) aur status
public class XeroConnectTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public XeroConnectTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Admin_gets_connect_url_pointing_to_login_xero_with_state()
    {
        await XeroTestHelpers.ResetAsync(_factory);
        var admin = await TestAuth.AdminClientAsync(_factory);

        var response = await admin.GetAsync("/api/Xero/connect-url");

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var json = await response.ReadJsonAsync();
        var url = json.GetProperty("url").GetString()!;
        var uri = new Uri(url);
        Assert.Equal("https", uri.Scheme);
        Assert.Equal("login.xero.com", uri.Host);
        Assert.Contains("response_type=code", url);
        Assert.Contains("client_id=xero-test-client", url);
        Assert.Contains("offline_access", url);
        Assert.False(string.IsNullOrWhiteSpace(XeroTestHelpers.QueryValue(url, "state")));
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("note").GetString()));
    }

    [Fact]
    public async Task FinanceManager_cannot_get_connect_url()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);

        var response = await client.GetAsync("/api/Xero/connect-url");

        await response.AssertStatusAsync(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Anonymous_cannot_get_connect_url()
    {
        var response = await _factory.CreateClient().GetAsync("/api/Xero/connect-url");

        await response.AssertStatusAsync(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Employee_cannot_read_status()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);

        var response = await client.GetAsync("/api/Xero/status");

        await response.AssertStatusAsync(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Callback_with_garbage_state_returns_400_without_calling_xero()
    {
        await XeroTestHelpers.ResetAsync(_factory);

        var response = await _factory.CreateClient()
            .GetAsync("/api/Xero/callback?code=abc&state=not-a-real-state");

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.False(string.IsNullOrWhiteSpace(await response.ReadMessageAsync()));
        Assert.Empty(_factory.XeroHttp.Requests);
    }

    [Fact]
    public async Task Callback_with_expired_state_returns_400()
    {
        await XeroTestHelpers.ResetAsync(_factory);
        var adminId = await TestAuth.GetAdminIdAsync(_factory);
        var pastUnixSeconds = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds();
        var state = XeroTestHelpers.Protector(_factory, XeroTestHelpers.StatePurpose)
            .Protect(adminId + "|" + pastUnixSeconds + "|nonce");

        var response = await _factory.CreateClient()
            .GetAsync($"/api/Xero/callback?code=abc&state={Uri.EscapeDataString(state)}");

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Contains("expire", await response.ReadMessageAsync());
        Assert.Empty(_factory.XeroHttp.Requests);
    }

    [Fact]
    public async Task Callback_without_code_returns_400()
    {
        await XeroTestHelpers.ResetAsync(_factory);
        var adminId = await TestAuth.GetAdminIdAsync(_factory);
        var state = XeroTestHelpers.BuildState(_factory, adminId, DateTimeOffset.UtcNow.AddMinutes(5));

        var response = await _factory.CreateClient()
            .GetAsync($"/api/Xero/callback?state={Uri.EscapeDataString(state)}");

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Empty(_factory.XeroHttp.Requests);
    }

    [Fact]
    public async Task Callback_with_xero_error_returns_400_with_the_error()
    {
        await XeroTestHelpers.ResetAsync(_factory);

        var response = await _factory.CreateClient()
            .GetAsync("/api/Xero/callback?error=access_denied");

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Contains("access_denied", await response.ReadMessageAsync());
        Assert.Empty(_factory.XeroHttp.Requests);
    }

    [Fact]
    public async Task Full_callback_stores_encrypted_tokens_audits_and_reports_connected()
    {
        await XeroTestHelpers.ResetAsync(_factory);
        var adminId = await TestAuth.GetAdminIdAsync(_factory);
        var admin = await TestAuth.AdminClientAsync(_factory);
        var tenantId = XeroTestHelpers.NewTenantId();

        // State asli connect-url se
        var connect = await admin.GetAsync("/api/Xero/connect-url");
        await connect.AssertStatusAsync(HttpStatusCode.OK);
        var url = (await connect.ReadJsonAsync()).GetProperty("url").GetString()!;
        var state = XeroTestHelpers.QueryValue(url, "state")!;

        _factory.XeroHttp.EnqueueJson(HttpStatusCode.OK, XeroTestHelpers.TokenJson("raw-access-1", "raw-refresh-1"));
        _factory.XeroHttp.EnqueueJson(HttpStatusCode.OK, XeroTestHelpers.ConnectionsJson(tenantId, "Demo Co"));

        var response = await _factory.CreateClient()
            .GetAsync($"/api/Xero/callback?code=auth-code-1&state={Uri.EscapeDataString(state)}");

        await response.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Contains("Demo Co", await response.ReadMessageAsync());

        // Xero calls: pehle code -> token, phir connections
        var requests = _factory.XeroHttp.Requests.ToArray();
        Assert.Equal(2, requests.Length);
        Assert.Equal(HttpMethod.Post, requests[0].Method);
        Assert.Equal(XeroTestHelpers.TokenUrl, requests[0].Uri!.AbsoluteUri);
        Assert.Equal(XeroTestHelpers.ExpectedBasicAuth(_factory), requests[0].Authorization);
        Assert.Contains("grant_type=authorization_code", requests[0].Body);
        Assert.Contains("code=auth-code-1", requests[0].Body);
        Assert.Equal(HttpMethod.Get, requests[1].Method);
        Assert.Equal(XeroTestHelpers.ConnectionsUrl, requests[1].Uri!.AbsoluteUri);
        Assert.Equal("Bearer raw-access-1", requests[1].Authorization);

        // Row: tokens encrypted (raw nahi), lekin wapis decrypt ho sakte hain
        var row = await XeroTestHelpers.FindConnectionAsync(_factory, tenantId);
        Assert.NotNull(row);
        Assert.Equal("Demo Co", row!.TenantName);
        Assert.Equal(adminId, row.ConnectedByUserId);
        Assert.NotEqual("raw-access-1", row.AccessTokenProtected);
        Assert.NotEqual("raw-refresh-1", row.RefreshTokenProtected);
        Assert.DoesNotContain("raw-access-1", row.AccessTokenProtected);
        Assert.DoesNotContain("raw-refresh-1", row.RefreshTokenProtected);
        var protector = XeroTestHelpers.TokenProtector(_factory);
        Assert.Equal("raw-access-1", protector.Unprotect(row.AccessTokenProtected));
        Assert.Equal("raw-refresh-1", protector.Unprotect(row.RefreshTokenProtected));
        Assert.True(row.AccessTokenExpiresAtUtc > DateTime.UtcNow.AddMinutes(20));

        // Audit: XeroConnected, state wale user ke naam
        var audit = await XeroTestHelpers.AuditRowsAsync(_factory, "XeroConnected", tenantId);
        var entry = Assert.Single(audit);
        Assert.Equal(adminId, entry.UserId);
        Assert.Equal("XeroConnection", entry.EntityType);

        // Status ab connected
        var status = await admin.GetAsync("/api/Xero/status");
        await status.AssertStatusAsync(HttpStatusCode.OK);
        var statusJson = await status.ReadJsonAsync();
        Assert.True(statusJson.GetProperty("connected").GetBoolean());
        Assert.Equal("Demo Co", statusJson.GetProperty("tenantName").GetString());
        Assert.True(statusJson.GetProperty("usingLiveData").GetBoolean());
        Assert.True(statusJson.TryGetProperty("connectedAtUtc", out _));
        Assert.True(statusJson.TryGetProperty("accessTokenExpiresAtUtc", out _));

        // Status kabhi token wapis nahi deta
        var rawStatus = statusJson.GetRawText();
        Assert.DoesNotContain("raw-access-1", rawStatus);
        Assert.DoesNotContain(row.AccessTokenProtected, rawStatus);
    }

    [Fact]
    public async Task Callback_when_token_exchange_fails_returns_400_and_stores_nothing()
    {
        await XeroTestHelpers.ResetAsync(_factory);
        var adminId = await TestAuth.GetAdminIdAsync(_factory);
        var state = XeroTestHelpers.BuildState(_factory, adminId, DateTimeOffset.UtcNow.AddMinutes(5));
        _factory.XeroHttp.EnqueueJson(HttpStatusCode.BadRequest, "{\"error\":\"invalid_grant\"}");

        var response = await _factory.CreateClient()
            .GetAsync($"/api/Xero/callback?code=bad-code&state={Uri.EscapeDataString(state)}");

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Single(_factory.XeroHttp.Requests);
        var count = await _factory.WithDbAsync(async db =>
        {
            return await db.XeroConnections.CountAsync();
        });
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Callback_when_xero_times_out_returns_504_and_stores_nothing()
    {
        await XeroTestHelpers.ResetAsync(_factory);
        var adminId = await TestAuth.GetAdminIdAsync(_factory);
        var state = XeroTestHelpers.BuildState(_factory, adminId, DateTimeOffset.UtcNow.AddMinutes(5));
        XeroTestHelpers.EnqueueTimeout(_factory);

        var response = await _factory.CreateClient()
            .GetAsync($"/api/Xero/callback?code=slow-code&state={Uri.EscapeDataString(state)}");

        await response.AssertStatusAsync(HttpStatusCode.GatewayTimeout);
        Assert.Contains("waqt", await response.ReadMessageAsync());
        var request = Assert.Single(_factory.XeroHttp.Requests);
        Assert.Equal(XeroTestHelpers.TokenUrl, request.Uri!.AbsoluteUri);
        var count = await _factory.WithDbAsync(async db =>
        {
            return await db.XeroConnections.CountAsync();
        });
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Callback_when_connections_call_times_out_returns_504()
    {
        await XeroTestHelpers.ResetAsync(_factory);
        var adminId = await TestAuth.GetAdminIdAsync(_factory);
        var state = XeroTestHelpers.BuildState(_factory, adminId, DateTimeOffset.UtcNow.AddMinutes(5));
        _factory.XeroHttp.EnqueueJson(HttpStatusCode.OK, XeroTestHelpers.TokenJson("t-access", "t-refresh"));
        XeroTestHelpers.EnqueueTimeout(_factory);

        var response = await _factory.CreateClient()
            .GetAsync($"/api/Xero/callback?code=c-slow&state={Uri.EscapeDataString(state)}");

        await response.AssertStatusAsync(HttpStatusCode.GatewayTimeout);
        Assert.Equal(2, _factory.XeroHttp.Requests.Count);
        var count = await _factory.WithDbAsync(async db =>
        {
            return await db.XeroConnections.CountAsync();
        });
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Callback_without_refresh_token_returns_400()
    {
        await XeroTestHelpers.ResetAsync(_factory);
        var adminId = await TestAuth.GetAdminIdAsync(_factory);
        var state = XeroTestHelpers.BuildState(_factory, adminId, DateTimeOffset.UtcNow.AddMinutes(5));
        _factory.XeroHttp.EnqueueJson(HttpStatusCode.OK, "{\"access_token\":\"only-access\",\"expires_in\":1800}");

        var response = await _factory.CreateClient()
            .GetAsync($"/api/Xero/callback?code=c1&state={Uri.EscapeDataString(state)}");

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Contains("offline_access", await response.ReadMessageAsync());
    }

    [Fact]
    public async Task Callback_with_no_organisation_returns_400()
    {
        await XeroTestHelpers.ResetAsync(_factory);
        var adminId = await TestAuth.GetAdminIdAsync(_factory);
        var state = XeroTestHelpers.BuildState(_factory, adminId, DateTimeOffset.UtcNow.AddMinutes(5));
        _factory.XeroHttp.EnqueueJson(HttpStatusCode.OK, XeroTestHelpers.TokenJson("a1", "r1"));
        _factory.XeroHttp.EnqueueJson(HttpStatusCode.OK, "[]");

        var response = await _factory.CreateClient()
            .GetAsync($"/api/Xero/callback?code=c1&state={Uri.EscapeDataString(state)}");

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        var count = await _factory.WithDbAsync(async db =>
        {
            return await db.XeroConnections.CountAsync();
        });
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Reconnecting_the_same_tenant_updates_the_existing_row()
    {
        await XeroTestHelpers.ResetAsync(_factory);
        var adminId = await TestAuth.GetAdminIdAsync(_factory);
        var existing = await XeroTestHelpers.SeedConnectionAsync(
            _factory, "old-access", "old-refresh", DateTime.UtcNow.AddMinutes(-10), tenantName: "Old Name");
        var state = XeroTestHelpers.BuildState(_factory, adminId, DateTimeOffset.UtcNow.AddMinutes(5));
        _factory.XeroHttp.EnqueueJson(HttpStatusCode.OK, XeroTestHelpers.TokenJson("new-access", "new-refresh"));
        _factory.XeroHttp.EnqueueJson(HttpStatusCode.OK, XeroTestHelpers.ConnectionsJson(existing.TenantId, "New Name"));

        var response = await _factory.CreateClient()
            .GetAsync($"/api/Xero/callback?code=c2&state={Uri.EscapeDataString(state)}");

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var rows = await _factory.WithDbAsync(async db =>
        {
            return await db.XeroConnections.AsNoTracking()
                .Where(c => c.TenantId == existing.TenantId)
                .ToListAsync();
        });
        var row = Assert.Single(rows);
        Assert.Equal(existing.Id, row.Id);
        Assert.Equal("New Name", row.TenantName);
        var protector = XeroTestHelpers.TokenProtector(_factory);
        Assert.Equal("new-access", protector.Unprotect(row.AccessTokenProtected));
        Assert.Equal("new-refresh", protector.Unprotect(row.RefreshTokenProtected));
    }

    // ---------- State sirf ek dafa, sirf Admin ka ----------

    [Fact]
    public async Task Used_state_cannot_be_replayed_with_another_code()
    {
        await XeroTestHelpers.ResetAsync(_factory);
        var admin = await TestAuth.AdminClientAsync(_factory);
        var connect = await admin.GetAsync("/api/Xero/connect-url");
        await connect.AssertStatusAsync(HttpStatusCode.OK);
        var url = (await connect.ReadJsonAsync()).GetProperty("url").GetString()!;
        var state = XeroTestHelpers.QueryValue(url, "state")!;
        var tenantId = XeroTestHelpers.NewTenantId();
        _factory.XeroHttp.EnqueueJson(HttpStatusCode.OK, XeroTestHelpers.TokenJson("first-access", "first-refresh"));
        _factory.XeroHttp.EnqueueJson(HttpStatusCode.OK, XeroTestHelpers.ConnectionsJson(tenantId, "First Co"));

        var first = await _factory.CreateClient()
            .GetAsync($"/api/Xero/callback?code=good-code&state={Uri.EscapeDataString(state)}");
        await first.AssertStatusAsync(HttpStatusCode.OK);

        // Leak hua link: koi aur apni Xero organisation ke code ke saath wahi state bhejta hai
        _factory.XeroHttp.Reset();
        var replay = await _factory.CreateClient()
            .GetAsync($"/api/Xero/callback?code=attacker-code&state={Uri.EscapeDataString(state)}");

        await replay.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Contains("pehle use", await replay.ReadMessageAsync());
        Assert.Empty(_factory.XeroHttp.Requests);
        var count = await _factory.WithDbAsync(async db =>
        {
            return await db.XeroConnections.CountAsync();
        });
        Assert.Equal(1, count);
        Assert.Single(await XeroTestHelpers.AuditRowsAsync(_factory, "XeroConnected", tenantId));
    }

    [Fact]
    public async Task State_is_used_up_even_when_the_first_callback_fails()
    {
        await XeroTestHelpers.ResetAsync(_factory);
        var adminId = await TestAuth.GetAdminIdAsync(_factory);
        var state = XeroTestHelpers.BuildState(_factory, adminId, DateTimeOffset.UtcNow.AddMinutes(5));
        _factory.XeroHttp.EnqueueJson(HttpStatusCode.BadRequest, "{\"error\":\"invalid_grant\"}");

        var first = await _factory.CreateClient()
            .GetAsync($"/api/Xero/callback?code=bad-code&state={Uri.EscapeDataString(state)}");
        await first.AssertStatusAsync(HttpStatusCode.BadRequest);

        _factory.XeroHttp.Reset();
        var second = await _factory.CreateClient()
            .GetAsync($"/api/Xero/callback?code=other-code&state={Uri.EscapeDataString(state)}");

        await second.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Contains("pehle use", await second.ReadMessageAsync());
        Assert.Empty(_factory.XeroHttp.Requests);
    }

    [Fact]
    public async Task Callback_with_state_of_a_non_admin_returns_400_without_calling_xero()
    {
        await XeroTestHelpers.ResetAsync(_factory);
        var manager = await TestAuth.CreateUserAsync(_factory, Roles.FinanceManager);
        var state = XeroTestHelpers.BuildState(_factory, manager.Id, DateTimeOffset.UtcNow.AddMinutes(5));

        var response = await _factory.CreateClient()
            .GetAsync($"/api/Xero/callback?code=c1&state={Uri.EscapeDataString(state)}");

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Contains("Sirf Admin", await response.ReadMessageAsync());
        Assert.Empty(_factory.XeroHttp.Requests);
        var count = await _factory.WithDbAsync(async db =>
        {
            return await db.XeroConnections.CountAsync();
        });
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Callback_with_state_of_a_demoted_admin_returns_400()
    {
        await XeroTestHelpers.ResetAsync(_factory);
        var formerAdmin = await TestAuth.CreateUserAsync(_factory, Roles.Admin);
        var state = XeroTestHelpers.BuildState(_factory, formerAdmin.Id, DateTimeOffset.UtcNow.AddMinutes(5));

        // Link banne ke baad role badal gaya
        var admin = await TestAuth.AdminClientAsync(_factory);
        var demote = await admin.PostAsJsonAsync($"/api/Users/{formerAdmin.Id}/role", new { role = Roles.Employee });
        await demote.AssertStatusAsync(HttpStatusCode.OK);

        var response = await _factory.CreateClient()
            .GetAsync($"/api/Xero/callback?code=c1&state={Uri.EscapeDataString(state)}");

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Contains("Sirf Admin", await response.ReadMessageAsync());
        Assert.Empty(_factory.XeroHttp.Requests);
    }

    [Fact]
    public async Task Status_without_connection_reports_not_connected()
    {
        await XeroTestHelpers.ResetAsync(_factory);
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);

        var response = await client.GetAsync("/api/Xero/status");

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var json = await response.ReadJsonAsync();
        Assert.False(json.GetProperty("connected").GetBoolean());
        Assert.False(json.GetProperty("usingLiveData").GetBoolean());
    }
}
