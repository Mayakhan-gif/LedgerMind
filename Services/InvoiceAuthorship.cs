using LedgerMind.Data;
using Microsoft.EntityFrameworkCore;

namespace LedgerMind.Services;

// Segregation of duties: jis ne invoice banaya, submit kiya ya Draft me badla (edit), wo khud
// approve/reject nahi karta. Edit ka record "InvoiceUpdated" audit me hota hai.
public static class InvoiceAuthorship
{
    public const string UpdatedAction = "InvoiceUpdated";
    private const string EntityType = "Invoice";

    // Kya is user ne ye invoice kabhi edit kiya?
    public static Task<bool> HasEditedAsync(
        AppDbContext db, int invoiceId, string userId, CancellationToken cancellationToken = default)
    {
        var entityId = invoiceId.ToString();
        return db.AuditLogs.AnyAsync(a =>
            a.EntityType == EntityType &&
            a.EntityId == entityId &&
            a.Action == UpdatedAction &&
            a.UserId == userId,
            cancellationToken);
    }

    // Is user ke edit kiye hue invoices ki ids (approvals queue se hatane ke liye)
    public static async Task<List<int>> EditedInvoiceIdsAsync(
        AppDbContext db, string userId, CancellationToken cancellationToken = default)
    {
        var entityIds = await db.AuditLogs
            .Where(a => a.EntityType == EntityType && a.Action == UpdatedAction && a.UserId == userId)
            .Select(a => a.EntityId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var ids = new List<int>();
        foreach (var entityId in entityIds)
        {
            if (int.TryParse(entityId, out var id))
                ids.Add(id);
        }

        return ids;
    }
}
