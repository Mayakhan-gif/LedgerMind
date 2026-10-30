namespace LedgerMind.Models;

public static class InvoiceStatus
{
    public const string Draft = "Draft";
    public const string PendingApproval = "PendingApproval";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
    public const string Partial = "Partial";
    public const string Paid = "Paid";
    public const string Cancelled = "Cancelled";
}