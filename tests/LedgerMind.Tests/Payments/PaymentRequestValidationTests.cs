using System.ComponentModel.DataAnnotations;
using LedgerMind.Models;

namespace LedgerMind.Tests.Payments;

public class PaymentRequestValidationTests
{
    private static List<ValidationResult> Validate(PaymentRequest model)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(model, new ValidationContext(model), results, validateAllProperties: true);
        return results;
    }

    private static PaymentRequest Valid() => new()
    {
        InvoiceId = 1,
        Amount = 100m,
        PaymentDate = new DateTime(2026, 10, 1),
        Method = "Cash"
    };

    private static bool HasError(List<ValidationResult> errors, string member) =>
        errors.Any(e => e.MemberNames.Contains(member));

    [Fact]
    public void Valid_request_has_no_errors()
    {
        Assert.Empty(Validate(Valid()));
    }

    [Fact]
    public void Two_decimal_amount_is_allowed()
    {
        var request = Valid();
        request.Amount = 1234.56m;

        Assert.Empty(Validate(request));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.001)]
    [InlineData(-1)]
    public void Amount_below_one_cent_is_rejected(double amount)
    {
        var request = Valid();
        request.Amount = (decimal)amount;

        Assert.True(HasError(Validate(request), nameof(PaymentRequest.Amount)));
    }

    [Fact]
    public void Amount_with_more_than_two_decimals_is_rejected()
    {
        var request = Valid();
        request.Amount = 10.005m;

        Assert.True(HasError(Validate(request), nameof(PaymentRequest.Amount)));
    }

    [Fact]
    public void Missing_payment_date_is_rejected()
    {
        var request = Valid();
        request.PaymentDate = default;

        Assert.True(HasError(Validate(request), nameof(PaymentRequest.PaymentDate)));
    }

    [Fact]
    public void Null_or_blank_method_is_allowed()
    {
        var withNull = Valid();
        withNull.Method = null;
        var withBlank = Valid();
        withBlank.Method = "   ";

        Assert.Empty(Validate(withNull));
        Assert.Empty(Validate(withBlank));
    }

    [Fact]
    public void Method_longer_than_30_characters_is_rejected()
    {
        var request = Valid();
        request.Method = new string('x', 31);

        Assert.True(HasError(Validate(request), nameof(PaymentRequest.Method)));
    }

    [Fact]
    public void Note_longer_than_300_characters_is_rejected()
    {
        var request = Valid();
        request.Note = new string('x', 301);

        Assert.True(HasError(Validate(request), nameof(PaymentRequest.Note)));
    }

    [Fact]
    public void Method_defaults_to_Cash()
    {
        Assert.Equal("Cash", new PaymentRequest().Method);
    }
}
