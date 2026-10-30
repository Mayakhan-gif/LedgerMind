using System.Net;
using System.Text.Json;
using LedgerMind.Models;
using LedgerMind.Tests.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace LedgerMind.Tests.Xero;

// GET /api/Xero/invoices: demo vs live, token refresh, pagination aur Xero ki ghaltiyan
public class XeroInvoicesTests : IClassFixture<TestWebApplicationFactory>
{
    private const string LiveInvoicesJson = """
        {
          "Id": "7d3a3d3c-0000-0000-0000-000000000000",
          "Status": "OK",
          "Invoices": [
            {
              "Type": "ACCREC",
              "InvoiceID": "inv-live-1",
              "InvoiceNumber": "INV-0101",
              "Contact": { "ContactID": "c-1", "Name": "Live Customer Ltd" },
              "Date": "/Date(1790000000000+0000)/",
              "DueDate": "/Date(1791590400000+0000)/",
              "DueDateString": "2026-10-10T00:00:00",
              "Status": "AUTHORISED",
              "Total": 1150.00,
              "AmountPaid": 150.00,
              "AmountDue": 1000.00,
              "CurrencyCode": "NZD"
            },
            {
              "Type": "ACCPAY",
              "InvoiceID": "inv-live-2",
              "InvoiceNumber": "BILL-0007",
              "Contact": { "Name": "Supplier Co" },
              "DueDate": "/Date(1518685950940+0000)/",
              "Status": "PAID",
              "Total": 250.5,
              "AmountPaid": 250.5,
              "AmountDue": 0,
              "CurrencyCode": "USD"
            }
          ]
        }
        """;

    private readonly TestWebApplicationFactory _factory;

    public XeroInvoicesTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static JsonElement InvoiceByNumber(JsonElement body, string invoiceNumber) =>
        body.GetProperty("invoices").EnumerateArray()
            .Single(i => i.GetProperty("invoiceNumber").GetString() == invoiceNumber);

    [Fact]
    public async Task Without_connection_invoices_are_demo_data()
    {
        await XeroTestHelpers.ResetAsync(_factory);
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);

        var response = await client.GetAsync("/api/Xero/invoices");

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var json = await response.ReadJsonAsync();
        Assert.True(json.GetProperty("isDemo").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("note").GetString()));
        Assert.Equal(4, json.GetProperty("count").GetInt32());
        var numbers = json.GetProperty("invoices").EnumerateArray()
            .Select(i => i.GetProperty("invoiceNumber").GetString())
            .ToList();
        Assert.Equal(4, numbers.Count);
        foreach (var expected in XeroTestHelpers.DemoInvoiceNumbers)
            Assert.Contains(expected, numbers);
        Assert.Empty(_factory.XeroHttp.Requests);
    }

    [Fact]
    public async Task Demo_invoices_can_be_filtered_by_status_case_insensitively()
    {
        await XeroTestHelpers.ResetAsync(_factory);
        var admin = await TestAuth.AdminClientAsync(_factory);

        var response = await admin.GetAsync("/api/Xero/invoices?status=paid");

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var json = await response.ReadJsonAsync();
        Assert.Equal(1, json.GetProperty("count").GetInt32());
        var invoice = Assert.Single(json.GetProperty("invoices").EnumerateArray().ToList());
        Assert.Equal("DEMO-INV-002", invoice.GetProperty("invoiceNumber").GetString());
    }

    [Fact]
    public async Task Employee_cannot_read_invoices()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);

        var response = await client.GetAsync("/api/Xero/invoices");

        await response.AssertStatusAsync(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Anonymous_cannot_read_invoices()
    {
        var response = await _factory.CreateClient().GetAsync("/api/Xero/invoices");

        await response.AssertStatusAsync(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task With_connection_invoices_come_from_xero_and_are_mapped()
    {
        await XeroTestHelpers.ResetAsync(_factory);
        var connection = await XeroTestHelpers.SeedConnectionAsync(
            _factory, "live-access", "live-refresh", DateTime.UtcNow.AddMinutes(30));
        _factory.XeroHttp.EnqueueJson(HttpStatusCode.OK, LiveInvoicesJson);
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);

        var response = await client.GetAsync("/api/Xero/invoices");

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var json = await response.ReadJsonAsync();
        Assert.False(json.GetProperty("isDemo").GetBoolean());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("note").ValueKind);
        Assert.Equal(2, json.GetProperty("count").GetInt32());

        var first = InvoiceByNumber(json, "INV-0101");
        Assert.Equal("inv-live-1", first.GetProperty("invoiceId").GetString());
        Assert.Equal("ACCREC", first.GetProperty("type").GetString());
        Assert.Equal("Live Customer Ltd", first.GetProperty("contactName").GetString());
        Assert.Equal("AUTHORISED", first.GetProperty("status").GetString());
        Assert.Equal(new DateTime(2026, 10, 10), first.GetProperty("dueDate").GetDateTime());
        Assert.Equal(1150.00m, first.GetProperty("total").GetDecimal());
        Assert.Equal(150.00m, first.GetProperty("amountPaid").GetDecimal());
        Assert.Equal(1000.00m, first.GetProperty("amountDue").GetDecimal());
        Assert.Equal("NZD", first.GetProperty("currencyCode").GetString());

        // DueDateString na ho to purana /Date(...)/ format
        var second = InvoiceByNumber(json, "BILL-0007");
        Assert.Equal("ACCPAY", second.GetProperty("type").GetString());
        Assert.Equal("Supplier Co", second.GetProperty("contactName").GetString());
        Assert.Equal("PAID", second.GetProperty("status").GetString());
        Assert.Equal(
            DateTimeOffset.FromUnixTimeMilliseconds(1518685950940).UtcDateTime,
            second.GetProperty("dueDate").GetDateTime());
        Assert.Equal(250.5m, second.GetProperty("total").GetDecimal());
        Assert.Equal(0m, second.GetProperty("amountDue").GetDecimal());

        // Request: Bearer token, tenant header, JSON accept, page 1 (token refresh nahi hua)
        var request = Assert.Single(_factory.XeroHttp.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(XeroTestHelpers.InvoicesUrl + "?page=1", request.Uri!.AbsoluteUri);
        Assert.Equal("Bearer live-access", request.Authorization);
        Assert.True(request.Headers.TryGetValue("xero-tenant-id", out var tenantHeader));
        Assert.Equal(connection.TenantId, tenantHeader);
        Assert.True(request.Headers.TryGetValue("Accept", out var accept));
        Assert.Contains("application/json", accept);
    }

    [Fact]
    public async Task Live_invoices_can_be_filtered_by_status()
    {
        await XeroTestHelpers.ResetAsync(_factory);
        await XeroTestHelpers.SeedConnectionAsync(_factory, "live-access", "live-refresh", DateTime.UtcNow.AddMinutes(30));
        _factory.XeroHttp.EnqueueJson(HttpStatusCode.OK, LiveInvoicesJson);
        var admin = await TestAuth.AdminClientAsync(_factory);

        var response = await admin.GetAsync("/api/Xero/invoices?status=PAID");

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var json = await response.ReadJsonAsync();
        Assert.False(json.GetProperty("isDemo").GetBoolean());
        var invoice = Assert.Single(json.GetProperty("invoices").EnumerateArray().ToList());
        Assert.Equal("BILL-0007", invoice.GetProperty("invoiceNumber").GetString());
    }

    [Fact]
    public async Task Invoices_follow_pages_until_a_short_page()
    {
        await XeroTestHelpers.ResetAsync(_factory);
        await XeroTestHelpers.SeedConnectionAsync(_factory, "page-access", "page-refresh", DateTime.UtcNow.AddMinutes(30));
        _factory.XeroHttp.EnqueueJson(HttpStatusCode.OK, XeroTestHelpers.InvoicesPageJson(100, "P1"));
        _factory.XeroHttp.EnqueueJson(HttpStatusCode.OK, XeroTestHelpers.InvoicesPageJson(3, "P2"));
        var admin = await TestAuth.AdminClientAsync(_factory);

        var response = await admin.GetAsync("/api/Xero/invoices");

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var json = await response.ReadJsonAsync();
        Assert.Equal(103, json.GetProperty("count").GetInt32());
        var urls = _factory.XeroHttp.Requests.Select(r => r.Uri!.AbsoluteUri).ToArray();
        Assert.Equal(
            new[] { XeroTestHelpers.InvoicesUrl + "?page=1", XeroTestHelpers.InvoicesUrl + "?page=2" },
            urls);
    }

    [Fact]
    public async Task Invoices_stop_after_five_full_pages()
    {
        await XeroTestHelpers.ResetAsync(_factory);
        await XeroTestHelpers.SeedConnectionAsync(_factory, "max-access", "max-refresh", DateTime.UtcNow.AddMinutes(30));
        for (var page = 1; page <= 5; page++)
            _factory.XeroHttp.EnqueueJson(HttpStatusCode.OK, XeroTestHelpers.InvoicesPageJson(100, $"M{page}"));
        var admin = await TestAuth.AdminClientAsync(_factory);

        var response = await admin.GetAsync("/api/Xero/invoices");

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var json = await response.ReadJsonAsync();
        Assert.Equal(500, json.GetProperty("count").GetInt32());
        Assert.Equal(5, _factory.XeroHttp.Requests.Count);
        Assert.Equal(XeroTestHelpers.InvoicesUrl + "?page=5", _factory.XeroHttp.Requests.Last().Uri!.AbsoluteUri);
    }

    [Fact]
    public async Task Expired_access_token_is_refreshed_and_rotated_before_reading_invoices()
    {
        await XeroTestHelpers.ResetAsync(_factory);
        var connection = await XeroTestHelpers.SeedConnectionAsync(
            _factory, "old-access", "old-refresh", DateTime.UtcNow.AddMinutes(-5));
        _factory.XeroHttp.EnqueueJson(HttpStatusCode.OK, XeroTestHelpers.TokenJson("new-access", "new-refresh", 1800));
        _factory.XeroHttp.EnqueueJson(HttpStatusCode.OK, XeroTestHelpers.InvoicesPageJson(1, "R"));
        var admin = await TestAuth.AdminClientAsync(_factory);

        var response = await admin.GetAsync("/api/Xero/invoices");

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var json = await response.ReadJsonAsync();
        Assert.False(json.GetProperty("isDemo").GetBoolean());
        Assert.Equal(1, json.GetProperty("count").GetInt32());

        var requests = _factory.XeroHttp.Requests.ToArray();
        Assert.Equal(2, requests.Length);

        // Pehle refresh
        Assert.Equal(HttpMethod.Post, requests[0].Method);
        Assert.Equal(XeroTestHelpers.TokenUrl, requests[0].Uri!.AbsoluteUri);
        Assert.Equal(XeroTestHelpers.ExpectedBasicAuth(_factory), requests[0].Authorization);
        Assert.Contains("grant_type=refresh_token", requests[0].Body);
        Assert.Contains("refresh_token=old-refresh", requests[0].Body);

        // Phir naye token se invoices
        Assert.Equal(XeroTestHelpers.InvoicesUrl + "?page=1", requests[1].Uri!.AbsoluteUri);
        Assert.Equal("Bearer new-access", requests[1].Authorization);

        // Naya (rotated) refresh token encrypted save hua
        var row = await XeroTestHelpers.FindConnectionAsync(_factory, connection.TenantId);
        Assert.NotNull(row);
        var protector = XeroTestHelpers.TokenProtector(_factory);
        Assert.Equal("new-access", protector.Unprotect(row!.AccessTokenProtected));
        Assert.Equal("new-refresh", protector.Unprotect(row.RefreshTokenProtected));
        Assert.True(row.AccessTokenExpiresAtUtc > DateTime.UtcNow.AddMinutes(25));
        Assert.True(row.UpdatedAtUtc >= connection.UpdatedAtUtc);
    }

    [Fact]
    public async Task Access_token_expiring_within_two_minutes_is_refreshed()
    {
        await XeroTestHelpers.ResetAsync(_factory);
        await XeroTestHelpers.SeedConnectionAsync(_factory, "soon-access", "soon-refresh", DateTime.UtcNow.AddMinutes(1));
        _factory.XeroHttp.EnqueueJson(HttpStatusCode.OK, XeroTestHelpers.TokenJson("fresh-access", "fresh-refresh"));
        _factory.XeroHttp.EnqueueJson(HttpStatusCode.OK, XeroTestHelpers.InvoicesPageJson(2, "S"));
        var admin = await TestAuth.AdminClientAsync(_factory);

        var response = await admin.GetAsync("/api/Xero/invoices");

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var requests = _factory.XeroHttp.Requests.ToArray();
        Assert.Equal(2, requests.Length);
        Assert.Contains("grant_type=refresh_token", requests[0].Body);
        Assert.Contains("refresh_token=soon-refresh", requests[0].Body);
        Assert.Equal("Bearer fresh-access", requests[1].Authorization);
    }

    [Fact]
    public async Task Refresh_failure_returns_409_and_keeps_stored_tokens()
    {
        await XeroTestHelpers.ResetAsync(_factory);
        var connection = await XeroTestHelpers.SeedConnectionAsync(
            _factory, "dead-access", "dead-refresh", DateTime.UtcNow.AddMinutes(-30));
        _factory.XeroHttp.EnqueueJson(HttpStatusCode.BadRequest, "{\"error\":\"invalid_grant\"}");
        var admin = await TestAuth.AdminClientAsync(_factory);

        var response = await admin.GetAsync("/api/Xero/invoices");

        await response.AssertStatusAsync(HttpStatusCode.Conflict);
        Assert.Contains("dobara connect", await response.ReadMessageAsync());

        // Sirf refresh ki koshish, invoices ki call nahi
        var request = Assert.Single(_factory.XeroHttp.Requests);
        Assert.Equal(XeroTestHelpers.TokenUrl, request.Uri!.AbsoluteUri);

        var row = await XeroTestHelpers.FindConnectionAsync(_factory, connection.TenantId);
        Assert.NotNull(row);
        Assert.Equal("dead-refresh", XeroTestHelpers.TokenProtector(_factory).Unprotect(row!.RefreshTokenProtected));
    }

    [Fact]
    public async Task Undecryptable_token_returns_409_without_calling_xero()
    {
        await XeroTestHelpers.ResetAsync(_factory);
        var connection = await XeroTestHelpers.SeedConnectionAsync(
            _factory, "x-access", "x-refresh", DateTime.UtcNow.AddMinutes(30));

        // Kisi aur purpose se encrypted = hamara protector nahi khol sakta (jaise key ring kho jaye)
        var foreign = XeroTestHelpers.Protector(_factory, "Some.Other.Purpose").Protect("x-access");
        await _factory.WithDbAsync(async db =>
        {
            var row = await db.XeroConnections.SingleAsync(c => c.TenantId == connection.TenantId);
            row.AccessTokenProtected = foreign;
            await db.SaveChangesAsync();
        });
        var admin = await TestAuth.AdminClientAsync(_factory);

        var response = await admin.GetAsync("/api/Xero/invoices");

        await response.AssertStatusAsync(HttpStatusCode.Conflict);
        Assert.Contains("dobara connect", await response.ReadMessageAsync());
        Assert.Empty(_factory.XeroHttp.Requests);
    }

    [Fact]
    public async Task Xero_api_server_error_returns_502()
    {
        await XeroTestHelpers.ResetAsync(_factory);
        await XeroTestHelpers.SeedConnectionAsync(_factory, "ok-access", "ok-refresh", DateTime.UtcNow.AddMinutes(30));
        _factory.XeroHttp.EnqueueJson(HttpStatusCode.InternalServerError, "{\"Message\":\"boom\"}");
        var admin = await TestAuth.AdminClientAsync(_factory);

        var response = await admin.GetAsync("/api/Xero/invoices");

        await response.AssertStatusAsync(HttpStatusCode.BadGateway);
        var message = await response.ReadMessageAsync();
        Assert.False(string.IsNullOrWhiteSpace(message));
        Assert.DoesNotContain("boom", message);
    }

    [Fact]
    public async Task Xero_api_unauthorized_returns_409_reconnect()
    {
        await XeroTestHelpers.ResetAsync(_factory);
        await XeroTestHelpers.SeedConnectionAsync(_factory, "revoked-access", "revoked-refresh", DateTime.UtcNow.AddMinutes(30));
        _factory.XeroHttp.EnqueueJson(HttpStatusCode.Unauthorized, "{\"Title\":\"Unauthorized\"}");
        var admin = await TestAuth.AdminClientAsync(_factory);

        var response = await admin.GetAsync("/api/Xero/invoices");

        await response.AssertStatusAsync(HttpStatusCode.Conflict);
        Assert.Contains("dobara connect", await response.ReadMessageAsync());
    }

    [Fact]
    public async Task Xero_api_timeout_returns_504()
    {
        await XeroTestHelpers.ResetAsync(_factory);
        await XeroTestHelpers.SeedConnectionAsync(_factory, "to-access", "to-refresh", DateTime.UtcNow.AddMinutes(30));
        XeroTestHelpers.EnqueueTimeout(_factory);
        var admin = await TestAuth.AdminClientAsync(_factory);

        var response = await admin.GetAsync("/api/Xero/invoices");

        await response.AssertStatusAsync(HttpStatusCode.GatewayTimeout);
        Assert.Contains("waqt", await response.ReadMessageAsync());
        var request = Assert.Single(_factory.XeroHttp.Requests);
        Assert.Equal(XeroTestHelpers.InvoicesUrl + "?page=1", request.Uri!.AbsoluteUri);
    }

    [Fact]
    public async Task Refresh_timeout_returns_504_and_keeps_stored_tokens()
    {
        await XeroTestHelpers.ResetAsync(_factory);
        var connection = await XeroTestHelpers.SeedConnectionAsync(
            _factory, "slow-access", "slow-refresh", DateTime.UtcNow.AddMinutes(-5));
        XeroTestHelpers.EnqueueTimeout(_factory);
        var admin = await TestAuth.AdminClientAsync(_factory);

        var response = await admin.GetAsync("/api/Xero/invoices");

        await response.AssertStatusAsync(HttpStatusCode.GatewayTimeout);
        Assert.Contains("waqt", await response.ReadMessageAsync());

        // Sirf refresh ki koshish, invoices ki call nahi
        var request = Assert.Single(_factory.XeroHttp.Requests);
        Assert.Equal(XeroTestHelpers.TokenUrl, request.Uri!.AbsoluteUri);

        var row = await XeroTestHelpers.FindConnectionAsync(_factory, connection.TenantId);
        Assert.NotNull(row);
        Assert.Equal("slow-refresh", XeroTestHelpers.TokenProtector(_factory).Unprotect(row!.RefreshTokenProtected));
    }

    [Fact]
    public async Task Failed_refresh_releases_the_lock_for_the_next_request()
    {
        await XeroTestHelpers.ResetAsync(_factory);
        await XeroTestHelpers.SeedConnectionAsync(_factory, "lock-access", "lock-refresh", DateTime.UtcNow.AddMinutes(-5));
        XeroTestHelpers.EnqueueTimeout(_factory);
        var admin = await TestAuth.AdminClientAsync(_factory);

        var first = await admin.GetAsync("/api/Xero/invoices");
        await first.AssertStatusAsync(HttpStatusCode.GatewayTimeout);

        // Lock chhoot gaya ho to doosri request foran refresh kar ke invoices laati hai
        _factory.XeroHttp.EnqueueJson(HttpStatusCode.OK, XeroTestHelpers.TokenJson("lock-new-access", "lock-new-refresh"));
        _factory.XeroHttp.EnqueueJson(HttpStatusCode.OK, XeroTestHelpers.InvoicesPageJson(1, "LOCK"));

        var second = await admin.GetAsync("/api/Xero/invoices");

        await second.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Equal(1, (await second.ReadJsonAsync()).GetProperty("count").GetInt32());
        var requests = _factory.XeroHttp.Requests.ToArray();
        Assert.Equal(3, requests.Length);
        Assert.Equal("Bearer lock-new-access", requests[2].Authorization);
    }

    [Fact]
    public async Task Status_with_connection_reports_live_data()
    {
        await XeroTestHelpers.ResetAsync(_factory);
        await XeroTestHelpers.SeedConnectionAsync(
            _factory, "s-access", "s-refresh", DateTime.UtcNow.AddMinutes(30), tenantName: "Status Co");
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);

        var response = await client.GetAsync("/api/Xero/status");

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var json = await response.ReadJsonAsync();
        Assert.True(json.GetProperty("connected").GetBoolean());
        Assert.True(json.GetProperty("usingLiveData").GetBoolean());
        Assert.Equal("Status Co", json.GetProperty("tenantName").GetString());
        Assert.Empty(_factory.XeroHttp.Requests);
    }
}
