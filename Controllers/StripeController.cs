using System.Security.Claims;
using LedgerMind.Models;
using LedgerMind.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace LedgerMind.Controllers;

[ApiController]
[Route("api/[controller]")]
public class StripeController : ControllerBase
{
    private readonly StripePaymentService _stripe;
    private readonly ILogger<StripeController> _logger;

    public StripeController(StripePaymentService stripe, ILogger<StripeController> logger)
    {
        _stripe = stripe;
        _logger = logger;
    }

    private string? CurrentUserId => User.FindFirstValue(ClaimTypes.NameIdentifier);

    [Authorize(Roles = Roles.Managers)]
    [HttpPost("checkout/{invoiceId}")]
    public async Task<IActionResult> CreateCheckout(int invoiceId)
    {
        try
        {
            var result = await _stripe.CreateCheckoutAsync(invoiceId, CurrentUserId);

            if (result.Error != null)
                return BadRequest(new { message = result.Error });

            return Ok(new
            {
                checkoutUrl = result.CheckoutUrl,
                attemptId = result.AttemptId,
                amount = result.Amount,
                currency = result.Currency,
                expiresAtUtc = result.ExpiresAtUtc
            });
        }
        catch (StripeConfigurationException ex)
        {
            _logger.LogError(ex, "Stripe configuration error");
            return StatusCode(500, new { message = "Stripe ki setting mukammal nahi." });
        }
        catch (Stripe.StripeException ex)
        {
            _logger.LogError(ex, "Stripe API error");
            return StatusCode(502, new { message = "Stripe ne request qabool nahi ki." });
        }
    }

    // Stripe yahan call karta hai. Login nahi hota, hifazat signature se hoti hai.
    [AllowAnonymous]
    [HttpPost("webhook")]
    public async Task<IActionResult> Webhook()
    {
        // Signature check ke liye body bilkul raw string honi chahiye
        using var reader = new StreamReader(Request.Body);
        var json = await reader.ReadToEndAsync();
        var signature = Request.Headers["Stripe-Signature"].ToString();

        try
        {
            var (status, message) = await _stripe.HandleWebhookAsync(json, signature);
            return StatusCode(status, new { message });
        }
        catch (StripeConfigurationException ex)
        {
            _logger.LogError(ex, "Stripe webhook configuration error");
            return StatusCode(500, new { message = "Webhook ki setting mukammal nahi." });
        }
    }

    // Online payments ki list (NeedsReview wale yahan se dhoondein)
    [Authorize(Roles = Roles.Managers)]
    [HttpGet("attempts")]
    public async Task<IActionResult> GetAttempts([FromQuery] string? status, [FromQuery] int? invoiceId)
    {
        var (items, error) = await _stripe.ListAttemptsAsync(status, invoiceId);
        if (error != null)
            return BadRequest(new { message = error });

        return Ok(items);
    }

    [Authorize(Roles = Roles.Managers)]
    [HttpPost("attempts/{id:int}/resolve")]
    public async Task<IActionResult> ResolveAttempt(int id, ResolveAttemptRequest request)
    {
        var result = await _stripe.ResolveAttemptAsync(id, request.Action, request.Note, CurrentUserId);
        return ToActionResult(result);
    }

    // Paisa wapis sirf admin (Stripe se asal paisa jata hai)
    [Authorize(Roles = Roles.Admin)]
    [HttpPost("attempts/{id:int}/refund")]
    public async Task<IActionResult> RefundAttempt(
        int id,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] RefundAttemptRequest? request)
    {
        try
        {
            var result = await _stripe.RefundAttemptAsync(id, request?.Note, CurrentUserId);
            return ToActionResult(result);
        }
        catch (StripeConfigurationException ex)
        {
            _logger.LogError(ex, "Stripe configuration error (refund)");
            return StatusCode(500, new { message = "Stripe ki setting mukammal nahi." });
        }
        catch (Stripe.StripeException ex)
        {
            _logger.LogError(ex, "Stripe refund error");
            return StatusCode(502, new { message = "Stripe ne refund qabool nahi kiya." });
        }
    }

    private IActionResult ToActionResult(StripeAttemptResult result)
    {
        switch (result.Kind)
        {
            case StripeAttemptResultKind.Ok:
                return Ok(result.Attempt);
            case StripeAttemptResultKind.NotFound:
                return NotFound(new { message = result.Message });
            case StripeAttemptResultKind.ProviderFailed:
                return StatusCode(502, new { message = result.Message });
            case StripeAttemptResultKind.Conflict:
                return Conflict(new { message = result.Message });
            default:
                return BadRequest(new { message = result.Message });
        }
    }
}
