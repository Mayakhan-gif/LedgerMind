namespace LedgerMind.Models;

public static class PaymentAttemptStatus
{
    public const string Pending = "Pending";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
    public const string Expired = "Expired";
    public const string NeedsReview = "NeedsReview";
    public const string Refunded = "Refunded";

    // NeedsReview ko kisi insan ne dekh kar band kar diya (paisa Stripe me khud wapis kiya, waghera)
    public const string Resolved = "Resolved";
}

public class PaymentAttempt
{
    public int Id { get; set; }
    public int InvoiceId { get; set; }
    public string Provider { get; set; } = "Stripe";
    public string ProviderSessionId { get; set; } = string.Empty;
    public string? ProviderPaymentIntentId { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string Status { get; set; } = PaymentAttemptStatus.Pending;
    public string? FailureReason { get; set; }
    public string? CreatedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; set; }

    // Ab tak kitna wapis (refund) hua, major units me (e.g. 1500.00)
    public decimal RefundedAmount { get; set; }
    public string? ProviderRefundId { get; set; }

    // NeedsReview ka faisla
    public string? ResolvedByUserId { get; set; }
    public DateTime? ResolvedAtUtc { get; set; }
    public string? ResolutionNote { get; set; }
}
