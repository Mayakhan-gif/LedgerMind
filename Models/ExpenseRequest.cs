using System.ComponentModel.DataAnnotations;

namespace LedgerMind.Models;

public class ExpenseRequest : IValidatableObject
{
    [Required]
    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string Category { get; set; } = string.Empty;

    [Range(0.01, 999999999)]
    public decimal Amount { get; set; }

    [Required]
    [StringLength(3, MinimumLength = 3)]
    [SupportedCurrency]
    public string Currency { get; set; } = "PKR";

    [Required]
    public DateTime ExpenseDate { get; set; }

    [MaxLength(500)]
    public string? Description { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        // Database decimal(18,2) hai: zyada decimal chupke se round ho jate
        if (MoneyRules.HasMoreThanTwoDecimals(Amount))
            yield return new ValidationResult(MoneyRules.DecimalsMessage, new[] { nameof(Amount) });
    }
}

// Approve/reject ke saath optional comment (expenses aur invoices dono)
public class ReviewRequest
{
    [MaxLength(500)]
    public string? Comment { get; set; }
}
