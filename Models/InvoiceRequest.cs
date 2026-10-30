using System.ComponentModel.DataAnnotations;

namespace LedgerMind.Models;

public class InvoiceRequest : IValidatableObject
{
    [Required]
    [MaxLength(50)]
    public string InvoiceNumber { get; set; } = string.Empty;

    [Required]
    public int CustomerId { get; set; }

    [MaxLength(500)]
    public string? Description { get; set; }

    [Required]
    public DateTime InvoiceDate { get; set; }

    [Required]
    public DateTime DueDate { get; set; }

    [Required]
    [StringLength(3, MinimumLength = 3)]
    [SupportedCurrency]
    public string Currency { get; set; } = "PKR";

    [Range(0.01, 999999999)]
    public decimal Subtotal { get; set; }

    [Range(0, 999999999)]
    public decimal TaxAmount { get; set; }

    public bool IsExcluded { get; set; } = false;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (DueDate.Date < InvoiceDate.Date)
            yield return new ValidationResult(
                "DueDate, InvoiceDate se pehle nahi ho sakti.",
                new[] { nameof(DueDate) });

        // Database decimal(18,2) hai: zyada decimal chupke se round ho kar Total aur Subtotal + Tax alag ho jate
        if (MoneyRules.HasMoreThanTwoDecimals(Subtotal))
            yield return new ValidationResult(MoneyRules.DecimalsMessage, new[] { nameof(Subtotal) });

        if (MoneyRules.HasMoreThanTwoDecimals(TaxAmount))
            yield return new ValidationResult(MoneyRules.DecimalsMessage, new[] { nameof(TaxAmount) });
    }
}
