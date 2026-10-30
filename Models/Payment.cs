namespace LedgerMind.Models;

public class Payment
{
    public int Id { get; set; }
    public int InvoiceId { get; set; }
    public Invoice? Invoice { get; set; }

    // Refund ki reversal entry me Amount manfi (negative) hota hai
    public decimal Amount { get; set; }
    public DateTime PaymentDate { get; set; }
    public string Method { get; set; } = "Cash";
    public string? Note { get; set; }

    // Stripe se aayi payment ka attempt (manual payment me null)
    public int? PaymentAttemptId { get; set; }
    public string? CreatedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
