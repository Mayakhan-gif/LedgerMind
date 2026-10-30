using LedgerMind.Models;

namespace LedgerMind.Services;

// FAKE data. Asli Xero se kuch nahi aata.
public class DemoXeroClient : IXeroClient
{
    public bool IsDemo => true;

    public Task<List<XeroInvoiceDto>> GetInvoicesAsync()
    {
        var today = DateTime.UtcNow.Date;

        var invoices = new List<XeroInvoiceDto>
        {
            new()
            {
                InvoiceId = "demo-0001",
                InvoiceNumber = "DEMO-INV-001",
                Type = "ACCREC",
                ContactName = "Demo Traders",
                Status = "AUTHORISED",
                DueDate = today.AddDays(-15),
                Total = 150000m,
                AmountPaid = 0m,
                AmountDue = 150000m,
                CurrencyCode = "PKR"
            },
            new()
            {
                InvoiceId = "demo-0002",
                InvoiceNumber = "DEMO-INV-002",
                Type = "ACCREC",
                ContactName = "Sample Enterprises",
                Status = "PAID",
                DueDate = today.AddDays(-30),
                Total = 80000m,
                AmountPaid = 80000m,
                AmountDue = 0m,
                CurrencyCode = "PKR"
            },
            new()
            {
                InvoiceId = "demo-0003",
                InvoiceNumber = "DEMO-BILL-001",
                Type = "ACCPAY",
                ContactName = "Demo Office Supplies",
                Status = "AUTHORISED",
                DueDate = today.AddDays(10),
                Total = 25000m,
                AmountPaid = 0m,
                AmountDue = 25000m,
                CurrencyCode = "PKR"
            },
            new()
            {
                InvoiceId = "demo-0004",
                InvoiceNumber = "DEMO-INV-003",
                Type = "ACCREC",
                ContactName = "Example Pvt Ltd",
                Status = "DRAFT",
                DueDate = today.AddDays(20),
                Total = 45000m,
                AmountPaid = 0m,
                AmountDue = 45000m,
                CurrencyCode = "PKR"
            }
        };

        return Task.FromResult(invoices);
    }
}