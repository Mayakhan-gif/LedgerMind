using System.Text.Json;
using System.Text.Json.Nodes;
using LedgerMind.Tests.Infrastructure.Fakes;

namespace LedgerMind.Tests.Chat;

// Groq (OpenAI jaise) jawab banane aur record hui requests parhne ke helpers
internal static class GroqFake
{
    // Model ka aakhri jawab (tool call ke baghair)
    public static string TextResponse(string? content)
    {
        var message = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = content
        };
        return Wrap(message, "stop");
    }

    // Model ek tool mangta hai (content null, jaisa asal API bhejti hai)
    public static string ToolCallResponse(string toolName, string arguments = "{}", string callId = "call_1") =>
        ToolCallsResponse(Call(callId, toolName, arguments));

    public static JsonObject Call(string callId, string toolName, JsonNode? arguments) => new()
    {
        ["id"] = callId,
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = toolName,
            ["arguments"] = arguments
        }
    };

    public static string ToolCallsResponse(params JsonObject[] calls)
    {
        var toolCalls = new JsonArray();
        foreach (var call in calls)
            toolCalls.Add(call);

        var message = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = null,
            ["tool_calls"] = toolCalls
        };
        return Wrap(message, "tool_calls");
    }

    private static string Wrap(JsonObject message, string finishReason)
    {
        var choice = new JsonObject
        {
            ["index"] = 0,
            ["message"] = message,
            ["finish_reason"] = finishReason
        };

        var choices = new JsonArray();
        choices.Add(choice);

        var root = new JsonObject { ["choices"] = choices };
        return root.ToJsonString();
    }

    // Groq ko bheje gaye "messages" (Clone, taake document band hone ke baad bhi chalein)
    public static List<JsonElement> Messages(RecordedRequest request)
    {
        Assert.NotNull(request.Body);
        using var doc = JsonDocument.Parse(request.Body!);
        return doc.RootElement.GetProperty("messages").EnumerateArray().Select(m => m.Clone()).ToList();
    }

    public static string? Role(JsonElement message) => message.GetProperty("role").GetString();

    public static List<JsonElement> ToolMessages(RecordedRequest request) =>
        Messages(request).Where(m => Role(m) == "tool").ToList();

    // Tool message ka content khud JSON text hota hai
    public static JsonElement ToolContent(JsonElement toolMessage)
    {
        var text = toolMessage.GetProperty("content").GetString();
        Assert.False(string.IsNullOrWhiteSpace(text));
        using var doc = JsonDocument.Parse(text!);
        return doc.RootElement.Clone();
    }

    public static JsonElement Body(RecordedRequest request)
    {
        Assert.NotNull(request.Body);
        using var doc = JsonDocument.Parse(request.Body!);
        return doc.RootElement.Clone();
    }
}
