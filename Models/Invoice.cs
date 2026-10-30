using System.Text.Json.Serialization;

namespace LedgerMind.Models;

public class Invoice
{
    public int Id { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;
    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public string? Description { get; set; }
    public DateTime InvoiceDate { get; set; }
    public DateTime DueDate { get; set; }
    public string Currency { get; set; } = "PKR";
    public decimal Subtotal { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal Total { get; set; }
    public string Status { get; set; } = InvoiceStatus.Draft;
    public bool IsExcluded { get; set; } = false;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    // Kis ne banaya, kis ne submit kiya, kis ne approve/reject kiya (segregation of duties)
    public string? CreatedByUserId { get; set; }
    public string? SubmittedByUserId { get; set; }
    public string? ReviewedByUserId { get; set; }
    public DateTime? ReviewedAtUtc { get; set; }
    public string? ReviewComment { get; set; }

    // SQL Server rowversion: do payments ek saath aayein to doosri 409 payegi
    [JsonIgnore]
    public byte[]? RowVersion { get; set; }
}
