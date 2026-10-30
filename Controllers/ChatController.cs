using LedgerMind.Models;
using LedgerMind.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace LedgerMind.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class ChatController : ControllerBase
{
    private readonly GroqChatService _chat;
    private readonly ILogger<ChatController> _logger;

    public ChatController(GroqChatService chat, ILogger<ChatController> logger)
    {
        _chat = chat;
        _logger = logger;
    }

    // Har user par had (AI ka kharcha), limit config se (RateLimiting:Chat*)
    [HttpPost]
    [EnableRateLimiting(RateLimitPolicies.Chat)]
    public async Task<IActionResult> Ask(ChatRequest request)
    {
        try
        {
            var reply = await _chat.AskAsync(request.Message, request.History, User, HttpContext.RequestAborted);
            return Ok(new { reply });
        }
        catch (ChatConfigurationException ex)
        {
            _logger.LogError(ex, "Chat configuration error");
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { message = "Chatbot ki setting mukammal nahi." });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Groq request failed");
            return StatusCode(StatusCodes.Status502BadGateway,
                new { message = "AI service abhi jawab nahi de rahi. Baad me koshish karein." });
        }
        catch (OperationCanceledException ex) when (!HttpContext.RequestAborted.IsCancellationRequested)
        {
            // HttpClient ka timeout (client ne khud request band nahi ki)
            _logger.LogWarning(ex, "Groq request timed out");
            return StatusCode(StatusCodes.Status502BadGateway,
                new { message = "AI service ne waqt par jawab nahi diya. Baad me koshish karein." });
        }
    }
}
