using System.ComponentModel.DataAnnotations;

namespace LedgerMind.Models;

public class PaymentRequest : IValidatableObject
{
    [Required]
    public int InvoiceId { get; set; }

    [Range(0.01, 999999999)]
    public decimal Amount { get; set; }

    [Required]
    public DateTime PaymentDate { get; set; }

    // Khali ya null ho to "Cash" (controller trim bhi karta hai)
    [MaxLength(30)]
    public string? Method { get; set; } = "Cash";

    [MaxLength(300)]
    public string? Note { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        // DateTime par [Required] kuch nahi pakarta: na bheji jaye to 0001-01-01 aa jati hai
        if (PaymentDate == default)
            yield return new ValidationResult(
                "PaymentDate zaroori hai.",
                new[] { nameof(PaymentDate) });

        // Database decimal(18,2) hai: 2 se zyada decimal chupke se round ho jate
        if (MoneyRules.HasMoreThanTwoDecimals(Amount))
            yield return new ValidationResult(MoneyRules.DecimalsMessage, new[] { nameof(Amount) });
    }
}
