using System.Net;
using System.Text.Json;
using LedgerMind.Models;
using LedgerMind.Tests.Infrastructure;

namespace LedgerMind.Tests.Reports;

// Is class ka apna (khaali) database hai aur sirf ek test, is liye yahan poore totals check ho sakte hain.
// Purane top-level totals sirf tab aate hain jab zyada se zyada ek currency ho.
public class ReportsSummaryCompatibilityTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public ReportsSummaryCompatibilityTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static async Task<JsonElement> GetSummaryAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/Reports/summary");
        await response.AssertStatusAsync(HttpStatusCode.OK);
        return await response.ReadJsonAsync();
    }

    [Fact]
    public async Task Top_level_totals_are_kept_only_while_a_single_currency_is_present()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);

        // Koi invoice nahi: currencies khaali aur totals 0
        var empty = await GetSummaryAsync(client);
        Assert.Equal(0, empty.GetProperty("currencies").GetArrayLength());
        Assert.Equal(0m, empty.GetProperty("totalInvoiced").GetDecimal());
        Assert.Equal(0m, empty.GetProperty("totalPaid").GetDecimal());
        Assert.Equal(0m, empty.GetProperty("totalReceivable").GetDecimal());
        Assert.Equal(0, empty.GetProperty("overdueCount").GetInt32());

        // Ek hi currency: top-level totals usi currency ki row ke barabar
        var customer = await TestData.AddCustomerAsync(_factory);
        var pkr = await TestData.AddInvoiceAsync(
            _factory, customer.Id, InvoiceStatus.Partial, subtotal: 1000m, currency: "PKR");
        await TestData.AddPaymentAsync(_factory, pkr.Id, 400m);

        var single = await GetSummaryAsync(client);
        var row = Assert.Single(single.GetProperty("currencies").EnumerateArray().ToList());
        Assert.Equal("PKR", row.GetProperty("currency").GetString());
        Assert.Equal(1000m, single.GetProperty("totalInvoiced").GetDecimal());
        Assert.Equal(400m, single.GetProperty("totalPaid").GetDecimal());
        Assert.Equal(600m, single.GetProperty("totalReceivable").GetDecimal());
        Assert.Equal(row.GetProperty("totalReceivable").GetDecimal(), single.GetProperty("totalReceivable").GetDecimal());

        // Doosri currency aate hi top-level totals null: currencies kabhi jama nahi hotin
        await TestData.AddInvoiceAsync(
            _factory, customer.Id, InvoiceStatus.Approved, subtotal: 50m, currency: "USD");

        var mixed = await GetSummaryAsync(client);
        Assert.Equal(2, mixed.GetProperty("currencies").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, mixed.GetProperty("totalInvoiced").ValueKind);
        Assert.Equal(JsonValueKind.Null, mixed.GetProperty("totalPaid").ValueKind);
        Assert.Equal(JsonValueKind.Null, mixed.GetProperty("totalReceivable").ValueKind);
        Assert.Equal(JsonValueKind.Number, mixed.GetProperty("overdueCount").ValueKind);
    }
}
