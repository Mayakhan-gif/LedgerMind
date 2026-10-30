namespace LedgerMind.Models;

// Xero se aane wale invoice ka sada sa naqsha (sirf wo fields jo hamein chahiye)
public class XeroInvoiceDto
{
    public string InvoiceId { get; set; } = string.Empty;
    public string InvoiceNumber { get; set; } = string.Empty;

    // Xero me: ACCREC = customer ko bheja gaya, ACCPAY = supplier se aaya
    public string Type { get; set; } = "ACCREC";

    public string ContactName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime? DueDate { get; set; }
    public decimal Total { get; set; }
    public decimal AmountPaid { get; set; }
    public decimal AmountDue { get; set; }
    public string CurrencyCode { get; set; } = string.Empty;
}