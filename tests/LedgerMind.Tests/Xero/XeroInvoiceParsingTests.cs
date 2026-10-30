using System.Text.Json;
using LedgerMind.Services;

namespace LedgerMind.Tests.Xero;

// XeroApiClient.ParseInvoices: Xero ke JSON se DTO (bina HTTP ke)
public class XeroInvoiceParsingTests
{
    [Fact]
    public void Maps_all_fields_from_a_xero_invoice()
    {
        const string json = """
            {
              "Invoices": [
                {
                  "Type": "ACCPAY",
                  "InvoiceID": "id-1",
                  "InvoiceNumber": "BILL-1",
                  "Contact": { "Name": "Supplier Ltd" },
                  "Status": "AUTHORISED",
                  "DueDateString": "2026-10-10T00:00:00",
                  "Total": 99.95,
                  "AmountPaid": 9.95,
                  "AmountDue": 90,
                  "CurrencyCode": "GBP"
                }
              ]
            }
            """;

        var invoice = Assert.Single(XeroApiClient.ParseInvoices(json));

        Assert.Equal("id-1", invoice.InvoiceId);
        Assert.Equal("BILL-1", invoice.InvoiceNumber);
        Assert.Equal("ACCPAY", invoice.Type);
        Assert.Equal("Supplier Ltd", invoice.ContactName);
        Assert.Equal("AUTHORISED", invoice.Status);
        Assert.NotNull(invoice.DueDate);
        var dueDate = invoice.DueDate!.Value;
        Assert.Equal(new DateTime(2026, 10, 10), dueDate);
        Assert.Equal(DateTimeKind.Utc, dueDate.Kind);
        Assert.Equal(99.95m, invoice.Total);
        Assert.Equal(9.95m, invoice.AmountPaid);
        Assert.Equal(90m, invoice.AmountDue);
        Assert.Equal("GBP", invoice.CurrencyCode);
    }

    [Fact]
    public void Missing_fields_get_defaults()
    {
        var invoice = Assert.Single(XeroApiClient.ParseInvoices("{\"Invoices\":[{}]}"));

        Assert.Equal(string.Empty, invoice.InvoiceId);
        Assert.Equal(string.Empty, invoice.InvoiceNumber);
        Assert.Equal("ACCREC", invoice.Type);
        Assert.Equal(string.Empty, invoice.ContactName);
        Assert.Equal(string.Empty, invoice.Status);
        Assert.Null(invoice.DueDate);
        Assert.Equal(0m, invoice.Total);
        Assert.Equal(0m, invoice.AmountPaid);
        Assert.Equal(0m, invoice.AmountDue);
        Assert.Equal(string.Empty, invoice.CurrencyCode);
    }

    [Fact]
    public void Due_date_falls_back_to_microsoft_json_date()
    {
        var invoice = Assert.Single(XeroApiClient.ParseInvoices(
            "{\"Invoices\":[{\"DueDate\":\"/Date(1518685950940+0000)/\"}]}"));

        Assert.NotNull(invoice.DueDate);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1518685950940).UtcDateTime, invoice.DueDate!.Value);
    }

    [Fact]
    public void Due_date_string_wins_over_due_date()
    {
        var invoice = Assert.Single(XeroApiClient.ParseInvoices(
            "{\"Invoices\":[{\"DueDate\":\"/Date(1518685950940+0000)/\",\"DueDateString\":\"2027-01-31T00:00:00\"}]}"));

        Assert.Equal(new DateTime(2027, 1, 31), invoice.DueDate!.Value);
    }

    [Fact]
    public void Unreadable_due_date_string_falls_back_to_due_date()
    {
        var invoice = Assert.Single(XeroApiClient.ParseInvoices(
            "{\"Invoices\":[{\"DueDate\":\"/Date(1518685950940)/\",\"DueDateString\":\"not a date\"}]}"));

        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1518685950940).UtcDateTime, invoice.DueDate!.Value);
    }

    [Fact]
    public void Unreadable_due_dates_become_null()
    {
        var invoice = Assert.Single(XeroApiClient.ParseInvoices(
            "{\"Invoices\":[{\"DueDate\":\"someday\",\"DueDateString\":\"\"}]}"));

        Assert.Null(invoice.DueDate);
    }

    [Fact]
    public void Wrong_value_types_are_ignored()
    {
        var invoice = Assert.Single(XeroApiClient.ParseInvoices(
            "{\"Invoices\":[{\"InvoiceNumber\":123,\"Total\":\"12.50\",\"Contact\":\"not-an-object\",\"Type\":null}]}"));

        Assert.Equal(string.Empty, invoice.InvoiceNumber);
        Assert.Equal(0m, invoice.Total);
        Assert.Equal(string.Empty, invoice.ContactName);
        Assert.Equal("ACCREC", invoice.Type);
    }

    [Fact]
    public void Non_object_items_are_skipped()
    {
        var invoices = XeroApiClient.ParseInvoices(
            "{\"Invoices\":[1,\"x\",null,{\"InvoiceNumber\":\"INV-9\"}]}");

        var invoice = Assert.Single(invoices);
        Assert.Equal("INV-9", invoice.InvoiceNumber);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"Invoices\":null}")]
    [InlineData("{\"Invoices\":{}}")]
    [InlineData("[]")]
    public void Missing_invoices_array_gives_empty_list(string json)
    {
        Assert.Empty(XeroApiClient.ParseInvoices(json));
    }

    [Fact]
    public void Invalid_json_throws_json_exception()
    {
        Assert.ThrowsAny<JsonException>(() => XeroApiClient.ParseInvoices("<html>not json</html>"));
    }
}
