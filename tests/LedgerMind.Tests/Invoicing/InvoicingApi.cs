using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LedgerMind.Models;
using LedgerMind.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace LedgerMind.Tests.Invoicing;

// Invoicing tests ke chhote madadgar (API calls aur seedha database checks)
internal static class InvoicingApi
{
    // React client jaisa InvoiceRequest body (isExcluded false ke saath)
    public static object InvoicePayload(
        int customerId,
        string? invoiceNumber = null,
        string currency = "PKR",
        decimal subtotal = 1000m,
        decimal taxAmount = 180m,
        DateTime? invoiceDate = null,
        DateTime? dueDate = null)
    {
        var date = invoiceDate ?? DateTime.UtcNow.Date;
        return new
        {
            invoiceNumber = invoiceNumber ?? TestData.UniqueInvoiceNumber(),
            customerId,
            description = "Invoicing test",
            invoiceDate = date,
            dueDate = dueDate ?? date.AddDays(30),
            currency,
            subtotal,
            taxAmount,
            isExcluded = false
        };
    }

    // API se Draft invoice banata hai aur uski id lautata hai
    public static async Task<int> CreateInvoiceAsync(HttpClient client, int customerId)
    {
        var response = await client.PostAsJsonAsync("/api/Invoices", InvoicePayload(customerId));
        await response.AssertStatusAsync(HttpStatusCode.Created);
        var json = await response.ReadJsonAsync();
        return json.GetProperty("id").GetInt32();
    }

    // React client ki tarah: POST baghair body (aur baghair Content-Type) ke
    public static Task<HttpResponseMessage> PostWithoutBodyAsync(HttpClient client, string url) =>
        client.PostAsync(url, null);

    public static Task<Invoice?> FindInvoiceAsync(TestWebApplicationFactory factory, int id) =>
        factory.WithDbAsync(db => db.Invoices.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id));

    public static async Task<Invoice> GetInvoiceAsync(TestWebApplicationFactory factory, int id)
    {
        var invoice = await FindInvoiceAsync(factory, id);
        Assert.NotNull(invoice);
        return invoice;
    }

    public static Task<List<AuditLog>> AuditRowsAsync(
        TestWebApplicationFactory factory, string action, string entityType, int entityId)
    {
        var id = entityId.ToString();
        return factory.WithDbAsync(db => db.AuditLogs
            .AsNoTracking()
            .Where(a => a.Action == action && a.EntityType == entityType && a.EntityId == id)
            .ToListAsync());
    }

    public static bool IsNull(this JsonElement json, string property) =>
        json.GetProperty(property).ValueKind == JsonValueKind.Null;

    public static List<int> Ids(this JsonElement json, string arrayProperty) =>
        json.GetProperty(arrayProperty)
            .EnumerateArray()
            .Select(e => e.GetProperty("id").GetInt32())
            .ToList();
}
