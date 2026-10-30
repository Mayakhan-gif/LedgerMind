using System.ComponentModel.DataAnnotations;

namespace LedgerMind.Models;

// Sirf 2-decimal currencies: Stripe ko raqam "× 100" minor units me jati hai,
// JPY (0 decimal) ya KWD (3 decimal) jaisi currency ghalat charge hoti.
public static class Currencies
{
    public static readonly IReadOnlyList<string> Supported =
        new[] { "PKR", "USD", "EUR", "GBP", "AED", "SAR", "CAD", "AUD" };

    public static bool IsSupported(string? code) =>
        !string.IsNullOrWhiteSpace(code) &&
        Supported.Contains(code.Trim().ToUpperInvariant());
}

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class SupportedCurrencyAttribute : ValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        // Khali value [Required] sambhalta hai
        if (value is null)
            return ValidationResult.Success;

        if (value is string code && Currencies.IsSupported(code))
            return ValidationResult.Success;

        var member = validationContext.MemberName ?? "Currency";
        return new ValidationResult(
            $"Currency in me se honi chahiye: {string.Join(", ", Currencies.Supported)}.",
            new[] { member });
    }
}
