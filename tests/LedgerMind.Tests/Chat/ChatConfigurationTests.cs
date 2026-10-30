using System.Net;
using System.Net.Http.Json;
using LedgerMind.Models;
using LedgerMind.Tests.Infrastructure;

namespace LedgerMind.Tests.Chat;

// Groq:ApiKey khali (jaise user-secrets set na hon)
public sealed class ChatWithoutApiKeyFactory : TestWebApplicationFactory
{
    public ChatWithoutApiKeyFactory()
    {
        Settings["Groq:ApiKey"] = "";
    }
}

public class ChatConfigurationTests : IClassFixture<ChatWithoutApiKeyFactory>
{
    private readonly ChatWithoutApiKeyFactory _factory;

    public ChatConfigurationTests(ChatWithoutApiKeyFactory factory)
    {
        _factory = factory;
        _factory.GroqHttp.Reset();
    }

    [Fact]
    public async Task Missing_groq_api_key_returns_500_without_calling_groq()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        _factory.GroqHttp.EnqueueJson(HttpStatusCode.OK, GroqFake.TextResponse("should not be used"));

        var response = await client.PostAsJsonAsync("/api/Chat", new { message = "hello" });

        await response.AssertStatusAsync(HttpStatusCode.InternalServerError);
        Assert.Equal("Chatbot ki setting mukammal nahi.", await response.ReadMessageAsync());
        Assert.Empty(_factory.GroqHttp.Requests);
    }
}
