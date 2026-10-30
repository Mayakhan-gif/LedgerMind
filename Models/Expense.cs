namespace LedgerMind.Models;

public class Expense
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "PKR";
    public DateTime ExpenseDate { get; set; }
    public string? Description { get; set; }
    public string Status { get; set; } = ExpenseStatus.Draft;
    public string SubmittedByUserId { get; set; } = string.Empty;
    public string? ReviewedByUserId { get; set; }
    public string? ReviewComment { get; set; }
    public DateTime? ReviewedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}