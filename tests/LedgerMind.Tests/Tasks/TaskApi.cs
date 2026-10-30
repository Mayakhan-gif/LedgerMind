using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LedgerMind.Models;
using LedgerMind.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace LedgerMind.Tests.Tasks;

// Task tests ke chhote helpers (API calls aur database checks)
internal static class TaskApi
{
    public static async Task<JsonElement> CreateAsync(HttpClient client, object body)
    {
        var response = await client.PostAsJsonAsync("/api/Tasks", body);
        await response.AssertStatusAsync(HttpStatusCode.Created);
        return await response.ReadJsonAsync();
    }

    public static Task<HttpResponseMessage> SetStatusAsync(HttpClient client, int id, string status) =>
        client.PostAsJsonAsync($"/api/Tasks/{id}/status", new { status });

    public static List<int> IdsOf(JsonElement array) =>
        array.EnumerateArray().Select(e => e.GetProperty("id").GetInt32()).ToList();

    public static async Task<List<int>> ListIdsAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        await response.AssertStatusAsync(HttpStatusCode.OK);
        return IdsOf(await response.ReadJsonAsync());
    }

    public static Task<TaskItem?> FindAsync(TestWebApplicationFactory factory, int id) =>
        factory.WithDbAsync<TaskItem?>(db => db.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id));
}
