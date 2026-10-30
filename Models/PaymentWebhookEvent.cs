namespace LedgerMind.Models;

public class PaymentWebhookEvent
{
    public int Id { get; set; }
    public string Provider { get; set; } = "Stripe";
    public string EventId { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public DateTime ReceivedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAtUtc { get; set; }
    public string? Outcome { get; set; }
}