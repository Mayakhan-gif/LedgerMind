using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace LedgerMind.Tests.Infrastructure;

// Asli Stripe jaisa event JSON aur "Stripe-Signature" header banata hai,
// taake webhook ka signature check bhi test me sach-much chale.
public static class StripeWebhookBuilder
{
    public static string CheckoutSessionEvent(
        string type,
        string sessionId,
        long amountTotalMinor,
        string currency,
        string paymentStatus = "paid",
        string? paymentIntentId = "pi_test_default",
        string? eventId = null)
    {
        var session = new JsonObject
        {
            ["id"] = sessionId,
            ["object"] = "checkout.session",
            ["amount_total"] = amountTotalMinor,
            ["currency"] = currency.ToLowerInvariant(),
            ["payment_status"] = paymentStatus,
            ["payment_intent"] = paymentIntentId,
            ["mode"] = "payment",
            ["status"] = paymentStatus == "paid" ? "complete" : "open"
        };

        return Wrap(eventId ?? NewEventId(), type, session);
    }

    public static string ChargeRefundedEvent(
        string paymentIntentId,
        long amountMinor,
        long amountRefundedMinor,
        string currency,
        string? eventId = null)
    {
        var charge = new JsonObject
        {
            ["id"] = $"ch_test_{Guid.NewGuid():N}",
            ["object"] = "charge",
            ["amount"] = amountMinor,
            ["amount_refunded"] = amountRefundedMinor,
            ["currency"] = currency.ToLowerInvariant(),
            ["payment_intent"] = paymentIntentId,
            ["refunded"] = amountRefundedMinor >= amountMinor,
            ["paid"] = true,
            ["status"] = "succeeded"
        };

        return Wrap(eventId ?? NewEventId(), "charge.refunded", charge);
    }

    // Stripe ka format: "t=<unix time>,v1=<hex HMAC-SHA256(secret, "<t>.<payload>")>"
    public static string Sign(string payload, string secret, DateTimeOffset? at = null)
    {
        var timestamp = (at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{timestamp}.{payload}"));
        return $"t={timestamp},v1={Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    // Tayyar request: body + sahi signature header
    public static HttpRequestMessage SignedRequest(string payload, string? secret = null, string? signatureOverride = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Stripe/webhook")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation(
            "Stripe-Signature",
            signatureOverride ?? Sign(payload, secret ?? TestWebApplicationFactory.StripeWebhookSecret));
        return request;
    }

    public static string NewEventId() => $"evt_test_{Guid.NewGuid():N}";

    private static string Wrap(string eventId, string type, JsonObject dataObject)
    {
        var stripeEvent = new JsonObject
        {
            ["id"] = eventId,
            ["object"] = "event",
            ["api_version"] = "2024-06-20",
            ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["livemode"] = false,
            ["pending_webhooks"] = 1,
            ["request"] = new JsonObject { ["id"] = null, ["idempotency_key"] = null },
            ["type"] = type,
            ["data"] = new JsonObject { ["object"] = dataObject }
        };

        return stripeEvent.ToJsonString();
    }
}
