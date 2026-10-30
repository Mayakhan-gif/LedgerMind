using System.Net;
using System.Net.Http.Json;
using LedgerMind.Models;
using LedgerMind.Tests.Infrastructure;

namespace LedgerMind.Tests.Chat;

// Chat ki had sirf 2 requests (lamba window, taake test ke beech reset na ho)
public sealed class ChatRateLimitFactory : TestWebApplicationFactory
{
    public ChatRateLimitFactory()
    {
        Settings["RateLimiting:ChatPermitLimit"] = "2";
        Settings["RateLimiting:ChatWindowSeconds"] = "3600";
    }
}

public class ChatRateLimitTests : IClassFixture<ChatRateLimitFactory>
{
    private readonly ChatRateLimitFactory _factory;

    public ChatRateLimitTests(ChatRateLimitFactory factory)
    {
        _factory = factory;
        _factory.GroqHttp.Reset();
    }

    private static Task<HttpResponseMessage> AskAsync(HttpClient client, string message) =>
        client.PostAsJsonAsync("/api/Chat", new { message });

    [Fact]
    public async Task Third_request_in_window_returns_429_without_calling_groq()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        _factory.GroqHttp.EnqueueJson(HttpStatusCode.OK, GroqFake.TextResponse("one"));
        _factory.GroqHttp.EnqueueJson(HttpStatusCode.OK, GroqFake.TextResponse("two"));

        var first = await AskAsync(client, "first");
        var second = await AskAsync(client, "second");
        var third = await AskAsync(client, "third");

        await first.AssertStatusAsync(HttpStatusCode.OK);
        await second.AssertStatusAsync(HttpStatusCode.OK);
        await third.AssertStatusAsync(HttpStatusCode.TooManyRequests);
        Assert.False(string.IsNullOrWhiteSpace(await third.ReadMessageAsync()));
        Assert.Equal(2, _factory.GroqHttp.Requests.Count);
    }

    [Fact]
    public async Task Limit_is_per_user()
    {
        var (_, busyClient) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var (_, otherClient) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        _factory.GroqHttp.EnqueueJson(HttpStatusCode.OK, GroqFake.TextResponse("one"));
        _factory.GroqHttp.EnqueueJson(HttpStatusCode.OK, GroqFake.TextResponse("two"));
        _factory.GroqHttp.EnqueueJson(HttpStatusCode.OK, GroqFake.TextResponse("other user"));

        await (await AskAsync(busyClient, "1")).AssertStatusAsync(HttpStatusCode.OK);
        await (await AskAsync(busyClient, "2")).AssertStatusAsync(HttpStatusCode.OK);
        await (await AskAsync(busyClient, "3")).AssertStatusAsync(HttpStatusCode.TooManyRequests);

        var response = await AskAsync(otherClient, "hello");

        await response.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Equal("other user", (await response.ReadJsonAsync()).GetProperty("reply").GetString());
    }
}
