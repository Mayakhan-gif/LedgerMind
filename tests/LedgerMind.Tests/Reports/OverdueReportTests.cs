using System.Net;
using System.Text.Json;
using LedgerMind.Models;
using LedgerMind.Tests.Infrastructure;

namespace LedgerMind.Tests.Reports;

public class OverdueReportTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public OverdueReportTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // ---------- Helpers ----------

    private async Task<HttpClient> ManagerClientAsync() =>
        (await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager)).Client;

    private static async Task<List<JsonElement>> GetOverdueAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/Reports/overdue");
        await response.AssertStatusAsync(HttpStatusCode.OK);
        var json = await response.ReadJsonAsync();
        Assert.Equal(JsonValueKind.Array, json.ValueKind);
        return json.EnumerateArray().ToList();
    }

    private async Task<(Customer Customer, Invoice Invoice)> AddInvoiceAsync(
        string status, decimal total, int dueInDays, string currency = "PKR", bool excluded = false)
    {
        var customer = await TestData.AddCustomerAsync(_factory);
        var today = DateTime.UtcNow.Date;
        var invoice = await TestData.AddInvoiceAsync(
            _factory,
            customer.Id,
            status,
            subtotal: total,
            currency: currency,
            invoiceDate: today.AddDays(-60),
            dueDate: today.AddDays(dueInDays),
            isExcluded: excluded);
        return (customer, invoice);
    }

    private static List<int> Ids(List<JsonElement> rows) =>
        rows.Select(r => r.GetProperty("id").GetInt32()).ToList();

    // ---------- Tests ----------

    [Fact]
    public async Task Lists_past_due_partial_invoice_with_paid_balance_and_days_overdue()
    {
        var client = await ManagerClientAsync();
        var (customer, invoice) = await AddInvoiceAsync(InvoiceStatus.Partial, 1000m, dueInDays: -10, currency: "USD");
        await TestData.AddPaymentAsync(_factory, invoice.Id, 250m);

        var rows = await GetOverdueAsync(client);

        var row = Assert.Single(rows, r => r.GetProperty("id").GetInt32() == invoice.Id);
        Assert.Equal(invoice.InvoiceNumber, row.GetProperty("invoiceNumber").GetString());
        Assert.Equal(customer.Name, row.GetProperty("customer").GetString());
        Assert.Equal("USD", row.GetProperty("currency").GetString());
        Assert.Equal(invoice.DueDate.Date, row.GetProperty("dueDate").GetDateTime().Date);
        Assert.Equal(1000m, row.GetProperty("amount").GetDecimal());
        Assert.Equal(250m, row.GetProperty("paid").GetDecimal());
        Assert.Equal(750m, row.GetProperty("balance").GetDecimal());
        Assert.Equal(InvoiceStatus.Partial, row.GetProperty("status").GetString());
        // Test aur server "aaj" alag alag nikalte hain: beech me UTC aadhi raat guzre to ek din zyada
        Assert.InRange(row.GetProperty("daysOverdue").GetInt32(), 10, 11);
    }

    [Fact]
    public async Task Lists_past_due_approved_invoice_with_full_balance()
    {
        var client = await ManagerClientAsync();
        var (_, invoice) = await AddInvoiceAsync(InvoiceStatus.Approved, 400m, dueInDays: -3);

        var rows = await GetOverdueAsync(client);

        var row = Assert.Single(rows, r => r.GetProperty("id").GetInt32() == invoice.Id);
        Assert.Equal(400m, row.GetProperty("amount").GetDecimal());
        Assert.Equal(0m, row.GetProperty("paid").GetDecimal());
        Assert.Equal(400m, row.GetProperty("balance").GetDecimal());
        Assert.Equal(InvoiceStatus.Approved, row.GetProperty("status").GetString());
        Assert.InRange(row.GetProperty("daysOverdue").GetInt32(), 3, 4);
    }

    [Fact]
    public async Task Refund_reversal_increases_the_overdue_balance()
    {
        var client = await ManagerClientAsync();
        var (_, invoice) = await AddInvoiceAsync(InvoiceStatus.Partial, 1000m, dueInDays: -7);
        await TestData.AddPaymentAsync(_factory, invoice.Id, 600m);
        await TestData.AddPaymentAsync(_factory, invoice.Id, -100m, method: "Stripe");

        var rows = await GetOverdueAsync(client);

        var row = Assert.Single(rows, r => r.GetProperty("id").GetInt32() == invoice.Id);
        Assert.Equal(500m, row.GetProperty("paid").GetDecimal());
        Assert.Equal(500m, row.GetProperty("balance").GetDecimal());
    }

    [Fact]
    public async Task Excludes_paid_excluded_future_and_unapproved_invoices()
    {
        var client = await ManagerClientAsync();
        var (_, paid) = await AddInvoiceAsync(InvoiceStatus.Paid, 500m, dueInDays: -10);
        await TestData.AddPaymentAsync(_factory, paid.Id, 500m);
        var (_, excluded) = await AddInvoiceAsync(InvoiceStatus.Approved, 500m, dueInDays: -10, excluded: true);
        var (_, future) = await AddInvoiceAsync(InvoiceStatus.Approved, 500m, dueInDays: 5);
        var (_, draft) = await AddInvoiceAsync(InvoiceStatus.Draft, 500m, dueInDays: -10);
        var (_, pending) = await AddInvoiceAsync(InvoiceStatus.PendingApproval, 500m, dueInDays: -10);
        var (_, rejected) = await AddInvoiceAsync(InvoiceStatus.Rejected, 500m, dueInDays: -10);
        var (_, cancelled) = await AddInvoiceAsync(InvoiceStatus.Cancelled, 500m, dueInDays: -10);
        var (_, overdue) = await AddInvoiceAsync(InvoiceStatus.Approved, 500m, dueInDays: -10);

        var ids = Ids(await GetOverdueAsync(client));

        Assert.Contains(overdue.Id, ids);
        Assert.DoesNotContain(paid.Id, ids);
        Assert.DoesNotContain(excluded.Id, ids);
        Assert.DoesNotContain(future.Id, ids);
        Assert.DoesNotContain(draft.Id, ids);
        Assert.DoesNotContain(pending.Id, ids);
        Assert.DoesNotContain(rejected.Id, ids);
        Assert.DoesNotContain(cancelled.Id, ids);
    }

    [Fact]
    public async Task Currency_is_normalized_like_the_summary()
    {
        var client = await ManagerClientAsync();
        // Purani row jisme currency chhote huroof me save hui thi
        var (_, invoice) = await AddInvoiceAsync(InvoiceStatus.Approved, 120m, dueInDays: -4, currency: "chf");

        var rows = await GetOverdueAsync(client);

        var row = Assert.Single(rows, r => r.GetProperty("id").GetInt32() == invoice.Id);
        Assert.Equal("CHF", row.GetProperty("currency").GetString());
        Assert.Equal(120m, row.GetProperty("balance").GetDecimal());
    }

    [Fact]
    public async Task List_is_ordered_by_due_date()
    {
        var client = await ManagerClientAsync();
        var (_, recent) = await AddInvoiceAsync(InvoiceStatus.Approved, 100m, dueInDays: -5);
        var (_, older) = await AddInvoiceAsync(InvoiceStatus.Approved, 100m, dueInDays: -20);

        var rows = await GetOverdueAsync(client);

        var ids = rows.Select(r => r.GetProperty("id").GetInt32()).ToList();
        Assert.True(ids.IndexOf(older.Id) >= 0);
        Assert.True(ids.IndexOf(older.Id) < ids.IndexOf(recent.Id));

        var dueDates = rows.Select(r => r.GetProperty("dueDate").GetDateTime()).ToList();
        Assert.Equal(dueDates.OrderBy(d => d).ToList(), dueDates);
    }

    [Fact]
    public async Task Overdue_balances_match_summary_overdue_amounts_per_currency()
    {
        var client = await ManagerClientAsync();
        var (_, partial) = await AddInvoiceAsync(InvoiceStatus.Partial, 900m, dueInDays: -12, currency: "GBP");
        await TestData.AddPaymentAsync(_factory, partial.Id, 400m);
        await AddInvoiceAsync(InvoiceStatus.Approved, 300m, dueInDays: -2, currency: "GBP");

        var rows = await GetOverdueAsync(client);
        var summaryResponse = await client.GetAsync("/api/Reports/summary");
        await summaryResponse.AssertStatusAsync(HttpStatusCode.OK);
        var summary = await summaryResponse.ReadJsonAsync();

        var fromList = rows
            .GroupBy(r => r.GetProperty("currency").GetString() ?? string.Empty)
            .ToDictionary(
                g => g.Key,
                g => (Count: g.Count(), Amount: g.Sum(r => r.GetProperty("balance").GetDecimal())));

        var currencyRows = summary.GetProperty("currencies").EnumerateArray().ToList();
        Assert.Equal(rows.Count, summary.GetProperty("overdueCount").GetInt32());
        foreach (var currencyRow in currencyRows)
        {
            var currency = currencyRow.GetProperty("currency").GetString() ?? string.Empty;
            // Is currency ka koi overdue na ho to (0, 0)
            fromList.TryGetValue(currency, out var expected);
            Assert.Equal(expected.Count, currencyRow.GetProperty("overdueCount").GetInt32());
            Assert.Equal(expected.Amount, currencyRow.GetProperty("overdueAmount").GetDecimal());
        }

        Assert.True(fromList.ContainsKey("GBP"));
        Assert.True(fromList["GBP"].Amount >= 800m);
    }

    [Fact]
    public async Task Employee_cannot_read_overdue_report()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);

        var response = await client.GetAsync("/api/Reports/overdue");

        await response.AssertStatusAsync(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Anonymous_cannot_read_overdue_report()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/Reports/overdue");

        await response.AssertStatusAsync(HttpStatusCode.Unauthorized);
    }
}
