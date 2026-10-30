using LedgerMind.Data;
using LedgerMind.Models;
using Microsoft.EntityFrameworkCore;

namespace LedgerMind.Services;

// Payment likhne ka ek hi rasta (manual, Stripe, refund reversal).
// Ye SaveChanges nahi karta: caller apne baqi changes ke saath ek hi dafa save karta hai.
// Invoice ka rowversion har dafa badalta hai, is liye do payments ek saath aayein
// to doosri DbUpdateConcurrencyException (409) payegi aur baqaya se zyada paisa nahi likha jayega.
public class PaymentLedger
{
    private readonly AppDbContext _db;

    public PaymentLedger(AppDbContext db)
    {
        _db = db;
    }

    public async Task<decimal> GetPaidAsync(int invoiceId, CancellationToken cancellationToken = default) =>
        await _db.Payments
            .Where(p => p.InvoiceId == invoiceId)
            .SumAsync(p => (decimal?)p.Amount, cancellationToken) ?? 0m;

    // amount manfi ho to refund ki reversal entry
    public Payment StagePayment(
        Invoice invoice,
        decimal paidBefore,
        decimal amount,
        DateTime paymentDate,
        string method,
        string? note,
        int? paymentAttemptId,
        string? userId)
    {
        var payment = new Payment
        {
            InvoiceId = invoice.Id,
            Amount = amount,
            PaymentDate = paymentDate,
            Method = method,
            Note = note,
            PaymentAttemptId = paymentAttemptId,
            CreatedByUserId = userId,
            CreatedAtUtc = DateTime.UtcNow
        };

        _db.Payments.Add(payment);
        ApplyStatus(invoice, paidBefore + amount);
        return payment;
    }

    // Kul jama raqam se status, aur invoice ko hamesha "modified" (rowversion check ke liye)
    public void ApplyStatus(Invoice invoice, decimal totalPaid)
    {
        invoice.Status = InvoiceRules.StatusForPaid(invoice.Total, totalPaid);
        _db.Entry(invoice).Property(i => i.Status).IsModified = true;
    }
}
