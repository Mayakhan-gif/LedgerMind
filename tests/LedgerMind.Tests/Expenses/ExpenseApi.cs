using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LedgerMind.Models;
using LedgerMind.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace LedgerMind.Tests.Expenses;

// Expense tests ke chhote helpers (API calls aur database checks)
internal static class ExpenseApi
{
    public const string DefaultTitle = "Taxi to client";

    public static object Body(
        string title = DefaultTitle,
        string currency = "PKR",
        decimal amount = 1500m,
        string category = "Travel") => new
    {
        title,
        category,
        amount,
        currency,
        expenseDate = DateTime.UtcNow.Date,
        description = "Client visit"
    };

    public static async Task<JsonElement> CreateAsync(
        HttpClient client, string title = DefaultTitle, string currency = "PKR")
    {
        var response = await client.PostAsJsonAsync("/api/Expenses", Body(title, currency));
        await response.AssertStatusAsync(HttpStatusCode.Created);
        return await response.ReadJsonAsync();
    }

    public static async Task<int> CreateIdAsync(HttpClient client, string title = DefaultTitle)
    {
        var json = await CreateAsync(client, title);
        return json.GetProperty("id").GetInt32();
    }

    // React client jaisa: { comment } body ke saath approve/reject
    public static Task<HttpResponseMessage> ReviewAsync(
        HttpClient client, int id, string action, string? comment = null) =>
        client.PostAsJsonAsync($"/api/Expenses/{id}/{action}", new { comment });

    public static List<int> IdsOf(JsonElement array) =>
        array.EnumerateArray().Select(e => e.GetProperty("id").GetInt32()).ToList();

    public static async Task<List<int>> ListIdsAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/Expenses");
        await response.AssertStatusAsync(HttpStatusCode.OK);
        return IdsOf(await response.ReadJsonAsync());
    }

    public static Task<Expense?> FindAsync(TestWebApplicationFactory factory, int id) =>
        factory.WithDbAsync<Expense?>(db => db.Expenses.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id));

    public static Task<List<AuditLog>> AuditsAsync(TestWebApplicationFactory factory, int expenseId)
    {
        var entityId = expenseId.ToString();
        return factory.WithDbAsync<List<AuditLog>>(db => db.AuditLogs.AsNoTracking()
            .Where(a => a.EntityType == "Expense" && a.EntityId == entityId)
            .OrderBy(a => a.Id)
            .ToListAsync());
    }
}
