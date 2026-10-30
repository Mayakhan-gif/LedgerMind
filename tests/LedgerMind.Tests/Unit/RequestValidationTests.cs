using System.ComponentModel.DataAnnotations;
using LedgerMind.Models;

namespace LedgerMind.Tests.Unit;

public class RequestValidationTests
{
    private static List<ValidationResult> Validate(object model)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(model, new ValidationContext(model), results, validateAllProperties: true);
        return results;
    }

    private static InvoiceRequest ValidInvoice() => new()
    {
        InvoiceNumber = "INV-1",
        CustomerId = 1,
        InvoiceDate = new DateTime(2026, 10, 1),
        DueDate = new DateTime(2026, 10, 31),
        Currency = "PKR",
        Subtotal = 100m,
        TaxAmount = 0m
    };

    [Theory]
    [InlineData("PKR")]
    [InlineData("usd")]
    [InlineData(" EUR ")]
    [InlineData("GBP")]
    [InlineData("AED")]
    public void Supported_currencies_are_accepted(string code)
    {
        Assert.True(Currencies.IsSupported(code));
    }

    [Theory]
    [InlineData("JPY")]
    [InlineData("KWD")]
    [InlineData("XXX")]
    [InlineData("")]
    [InlineData(null)]
    public void Unsupported_or_empty_currencies_are_rejected(string? code)
    {
        Assert.False(Currencies.IsSupported(code));
    }

    [Fact]
    public void Valid_invoice_request_has_no_errors()
    {
        Assert.Empty(Validate(ValidInvoice()));
    }

    [Fact]
    public void Invoice_due_date_before_invoice_date_is_rejected()
    {
        var request = ValidInvoice();
        request.DueDate = request.InvoiceDate.AddDays(-1);

        var errors = Validate(request);

        Assert.Contains(errors, e => e.MemberNames.Contains(nameof(InvoiceRequest.DueDate)));
    }

    [Fact]
    public void Invoice_due_date_on_same_day_is_allowed()
    {
        var request = ValidInvoice();
        request.DueDate = request.InvoiceDate;

        Assert.Empty(Validate(request));
    }

    [Fact]
    public void Invoice_with_zero_decimal_currency_is_rejected()
    {
        var request = ValidInvoice();
        request.Currency = "JPY";

        var errors = Validate(request);

        Assert.Contains(errors, e => e.MemberNames.Contains(nameof(InvoiceRequest.Currency)));
    }

    [Fact]
    public void Expense_with_unsupported_currency_is_rejected()
    {
        var request = new ExpenseRequest
        {
            Title = "Taxi",
            Category = "Travel",
            Amount = 10m,
            Currency = "KWD",
            ExpenseDate = new DateTime(2026, 10, 1)
        };

        var errors = Validate(request);

        Assert.Contains(errors, e => e.MemberNames.Contains(nameof(ExpenseRequest.Currency)));
    }
}
