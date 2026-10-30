using System.Net;
using System.Text.Json;
using LedgerMind.Models;
using LedgerMind.Tests.Infrastructure;

namespace LedgerMind.Tests.Reports;

// Database is class ke sab tests me shared hai, is liye har test pehle/baad ka farq (delta) dekhta hai
public class ReportsSummaryTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public ReportsSummaryTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private sealed record CurrencyTotals(
        int InvoiceCount,
        decimal TotalInvoiced,
        decimal TotalPaid,
        decimal TotalReceivable,
        int OverdueCount,
        decimal OverdueAmount)
    {
        public static readonly CurrencyTotals None = new(0, 0m, 0m, 0m, 0, 0m);

        public CurrencyTotals Minus(CurrencyTotals other) => new(
            InvoiceCount - other.InvoiceCount,
            TotalInvoiced - other.TotalInvoiced,
            TotalPaid - other.TotalPaid,
            TotalReceivable - other.TotalReceivable,
            OverdueCount - other.OverdueCount,
            OverdueAmount - other.OverdueAmount);
    }

    private sealed class Summary
    {
        public Dictionary<string, CurrencyTotals> Rows { get; } = new();
        public List<string> Order { get; } = new();
        public int OverdueCount { get; set; }

        public CurrencyTotals For(string currency) =>
            Rows.TryGetValue(currency, out var row) ? row : CurrencyTotals.None;
    }

    // ---------- Helpers ----------

    private async Task<HttpClient> ManagerClientAsync() =>
        (await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager)).Client;

    private static async Task<Summary> GetSummaryAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/Reports/summary");
        await response.AssertStatusAsync(HttpStatusCode.OK);
        var json = await response.ReadJsonAsync();

        var summary = new Summary { OverdueCount = json.GetProperty("overdueCount").GetInt32() };
        foreach (var row in json.GetProperty("currencies").EnumerateArray())
        {
            var currency = row.GetProperty("currency").GetString() ?? string.Empty;
            summary.Order.Add(currency);
            summary.Rows[currency] = new CurrencyTotals(
                row.GetProperty("invoiceCount").GetInt32(),
                row.GetProperty("totalInvoiced").GetDecimal(),
                row.GetProperty("totalPaid").GetDecimal(),
                row.GetProperty("totalReceivable").GetDecimal(),
                row.GetProperty("overdueCount").GetInt32(),
                row.GetProperty("overdueAmount").GetDecimal());
        }

        return summary;
    }

    private async Task<Invoice> AddInvoiceAsync(
        string status, decimal total, string currency, int dueInDays = 30, bool excluded = false)
    {
        var customer = await TestData.AddCustomerAsync(_factory);
        var today = DateTime.UtcNow.Date;
        return await TestData.AddInvoiceAsync(
            _factory,
            customer.Id,
            status,
            subtotal: total,
            currency: currency,
            invoiceDate: today.AddDays(-60),
            dueDate: today.AddDays(dueInDays),
            isExcluded: excluded);
    }

    private static void AssertRowsUnchanged(Summary before, Summary after, params string[] except)
    {
        var currencies = before.Rows.Keys.Union(after.Rows.Keys).Where(c => !except.Contains(c));
        foreach (var currency in currencies)
            Assert.Equal(before.For(currency), after.For(currency));
    }

    // ---------- Tests ----------

    [Fact]
    public async Task Approved_invoices_change_only_their_own_currency_rows()
    {
        var client = await ManagerClientAsync();
        var before = await GetSummaryAsync(client);

        await AddInvoiceAsync(InvoiceStatus.Approved, 1000m, "PKR");
        await AddInvoiceAsync(InvoiceStatus.Approved, 250m, "USD");

        var after = await GetSummaryAsync(client);

        Assert.Equal(new CurrencyTotals(1, 1000m, 0m, 1000m, 0, 0m), after.For("PKR").Minus(before.For("PKR")));
        Assert.Equal(new CurrencyTotals(1, 250m, 0m, 250m, 0, 0m), after.For("USD").Minus(before.For("USD")));
        AssertRowsUnchanged(before, after, "PKR", "USD");
        Assert.Equal(before.OverdueCount, after.OverdueCount);
    }

    [Fact]
    public async Task Draft_pending_rejected_cancelled_and_excluded_invoices_do_not_change_totals()
    {
        var client = await ManagerClientAsync();
        var before = await GetSummaryAsync(client);

        await AddInvoiceAsync(InvoiceStatus.Draft, 100m, "PKR", dueInDays: -10);
        await AddInvoiceAsync(InvoiceStatus.PendingApproval, 200m, "PKR", dueInDays: -10);
        await AddInvoiceAsync(InvoiceStatus.Rejected, 300m, "USD", dueInDays: -10);
        var cancelled = await AddInvoiceAsync(InvoiceStatus.Cancelled, 400m, "PKR", dueInDays: -10);
        await TestData.AddPaymentAsync(_factory, cancelled.Id, 50m);
        var excludedApproved = await AddInvoiceAsync(InvoiceStatus.Approved, 500m, "PKR", dueInDays: -10, excluded: true);
        await TestData.AddPaymentAsync(_factory, excludedApproved.Id, 100m);
        var excludedPaid = await AddInvoiceAsync(InvoiceStatus.Paid, 600m, "GBP", excluded: true);
        await TestData.AddPaymentAsync(_factory, excludedPaid.Id, 600m);

        var after = await GetSummaryAsync(client);

        Assert.Equal(before.Order, after.Order);
        AssertRowsUnchanged(before, after);
        Assert.Equal(before.OverdueCount, after.OverdueCount);
    }

    [Fact]
    public async Task Paid_invoice_counts_in_invoiced_and_paid_but_not_in_receivable_or_overdue()
    {
        var client = await ManagerClientAsync();
        var before = await GetSummaryAsync(client);

        var paid = await AddInvoiceAsync(InvoiceStatus.Paid, 500m, "EUR", dueInDays: -15);
        await TestData.AddPaymentAsync(_factory, paid.Id, 500m);

        var after = await GetSummaryAsync(client);

        Assert.Equal(new CurrencyTotals(1, 500m, 500m, 0m, 0, 0m), after.For("EUR").Minus(before.For("EUR")));
        AssertRowsUnchanged(before, after, "EUR");
        Assert.Equal(before.OverdueCount, after.OverdueCount);
    }

    [Fact]
    public async Task Receivable_is_invoiced_minus_paid_including_refund_reversals()
    {
        var client = await ManagerClientAsync();
        var before = await GetSummaryAsync(client);

        var partial = await AddInvoiceAsync(InvoiceStatus.Partial, 1000m, "AED");
        await TestData.AddPaymentAsync(_factory, partial.Id, 600m);
        // Refund ki reversal entry (manfi) paid ko kam karti hai
        await TestData.AddPaymentAsync(_factory, partial.Id, -100m, method: "Stripe");
        await AddInvoiceAsync(InvoiceStatus.Approved, 300m, "AED");

        var after = await GetSummaryAsync(client);

        Assert.Equal(new CurrencyTotals(2, 1300m, 500m, 800m, 0, 0m), after.For("AED").Minus(before.For("AED")));
        AssertRowsUnchanged(before, after, "AED");
    }

    [Fact]
    public async Task Overdue_count_and_amount_use_remaining_balance_of_past_due_invoices()
    {
        var client = await ManagerClientAsync();
        var before = await GetSummaryAsync(client);

        await AddInvoiceAsync(InvoiceStatus.Approved, 1000m, "SAR", dueInDays: -5);
        var partial = await AddInvoiceAsync(InvoiceStatus.Partial, 800m, "SAR", dueInDays: -20);
        await TestData.AddPaymentAsync(_factory, partial.Id, 300m);
        await AddInvoiceAsync(InvoiceStatus.Approved, 200m, "SAR", dueInDays: 5);

        var after = await GetSummaryAsync(client);

        Assert.Equal(new CurrencyTotals(3, 2000m, 300m, 1700m, 2, 1500m), after.For("SAR").Minus(before.For("SAR")));
        Assert.Equal(before.OverdueCount + 2, after.OverdueCount);
        AssertRowsUnchanged(before, after, "SAR");
    }

    [Fact]
    public async Task Lowercase_currency_is_grouped_under_the_upper_case_code()
    {
        var client = await ManagerClientAsync();
        var before = await GetSummaryAsync(client);

        // Purani row jisme currency chhote huroof me save hui thi
        await AddInvoiceAsync(InvoiceStatus.Approved, 70m, "nzd");
        await AddInvoiceAsync(InvoiceStatus.Approved, 30m, "NZD");

        var after = await GetSummaryAsync(client);

        Assert.DoesNotContain("nzd", after.Order);
        Assert.Equal(new CurrencyTotals(2, 100m, 0m, 100m, 0, 0m), after.For("NZD").Minus(before.For("NZD")));
        AssertRowsUnchanged(before, after, "NZD");
    }

    [Fact]
    public async Task Rows_are_ordered_by_currency_and_totals_are_consistent()
    {
        var client = await ManagerClientAsync();
        await AddInvoiceAsync(InvoiceStatus.Approved, 100m, "USD", dueInDays: -3);
        await AddInvoiceAsync(InvoiceStatus.Approved, 100m, "CAD", dueInDays: -3);
        await AddInvoiceAsync(InvoiceStatus.Partial, 100m, "AUD");

        var summary = await GetSummaryAsync(client);

        Assert.True(summary.Order.Count >= 3);
        Assert.Equal(summary.Order.OrderBy(c => c, StringComparer.Ordinal).ToList(), summary.Order);
        Assert.Equal(summary.Order.Count, summary.Order.Distinct().Count());
        Assert.Equal(summary.Rows.Values.Sum(r => r.OverdueCount), summary.OverdueCount);
        Assert.All(summary.Rows.Values, r => Assert.Equal(r.TotalInvoiced - r.TotalPaid, r.TotalReceivable));
    }

    [Fact]
    public async Task Summary_carries_generated_at_timestamp()
    {
        var client = await ManagerClientAsync();

        var response = await client.GetAsync("/api/Reports/summary");

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var json = await response.ReadJsonAsync();
        Assert.Equal(JsonValueKind.Array, json.GetProperty("currencies").ValueKind);
        var generated = json.GetProperty("generatedAtUtc").GetDateTimeOffset();
        Assert.True(Math.Abs((DateTimeOffset.UtcNow - generated).TotalMinutes) < 5);
    }

    [Fact]
    public async Task Admin_can_read_summary()
    {
        var client = await TestAuth.AdminClientAsync(_factory);

        var response = await client.GetAsync("/api/Reports/summary");

        await response.AssertStatusAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Employee_cannot_read_summary()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);

        var response = await client.GetAsync("/api/Reports/summary");

        await response.AssertStatusAsync(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Anonymous_cannot_read_summary()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/Reports/summary");

        await response.AssertStatusAsync(HttpStatusCode.Unauthorized);
    }
}
