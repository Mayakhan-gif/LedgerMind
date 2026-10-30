using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace LedgerMind.Tests.Infrastructure;

public static class HttpAssertions
{
    // Status code ghalat ho to jawab ka body bhi failure message me dikhe
    public static async Task AssertStatusAsync(this HttpResponseMessage response, HttpStatusCode expected)
    {
        if (response.StatusCode != expected)
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.Fail($"Expected {(int)expected} {expected} but got {(int)response.StatusCode} {response.StatusCode}. Body: {body}");
        }
    }

    public static async Task<JsonElement> ReadJsonAsync(this HttpResponseMessage response)
    {
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json;
    }

    // { "message": "..." } wale jawab ka message
    public static async Task<string?> ReadMessageAsync(this HttpResponseMessage response)
    {
        var json = await response.ReadJsonAsync();
        return json.ValueKind == JsonValueKind.Object && json.TryGetProperty("message", out var message)
            ? message.GetString()
            : null;
    }
}
