using System.ComponentModel.DataAnnotations;

namespace LedgerMind.Models;

// NeedsReview attempt ka faisla: "apply" (payment darj karo) ya "dismiss" (sirf band karo)
public class ResolveAttemptRequest
{
    public const string Apply = "apply";
    public const string Dismiss = "dismiss";

    [Required]
    [MaxLength(20)]
    public string Action { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? Note { get; set; }
}

// Stripe se paisa wapis (body khali bhi chal jati hai)
public class RefundAttemptRequest
{
    [MaxLength(500)]
    public string? Note { get; set; }
}

// Stripe attempts ki list aur resolve/refund ka jawab
public sealed record StripeAttemptSummary(
    int Id,
    int InvoiceId,
    string? InvoiceNumber,
    decimal Amount,
    string Currency,
    string Status,
    string? FailureReason,
    decimal RefundedAmount,
    string ProviderSessionId,
    string? ProviderPaymentIntentId,
    string? ProviderRefundId,
    string? CreatedByUserId,
    DateTime CreatedAtUtc,
    DateTime? CompletedAtUtc,
    string? ResolvedByUserId,
    DateTime? ResolvedAtUtc,
    string? ResolutionNote)
{
    public static StripeAttemptSummary From(PaymentAttempt attempt, string? invoiceNumber) => new(
        attempt.Id,
        attempt.InvoiceId,
        invoiceNumber,
        attempt.Amount,
        attempt.Currency,
        attempt.Status,
        attempt.FailureReason,
        attempt.RefundedAmount,
        attempt.ProviderSessionId,
        attempt.ProviderPaymentIntentId,
        attempt.ProviderRefundId,
        attempt.CreatedByUserId,
        attempt.CreatedAtUtc,
        attempt.CompletedAtUtc,
        attempt.ResolvedByUserId,
        attempt.ResolvedAtUtc,
        attempt.ResolutionNote);
}
