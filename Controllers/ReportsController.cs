using LedgerMind.Data;
using LedgerMind.Models;
using LedgerMind.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LedgerMind.Controllers;

[Authorize(Roles = Roles.Managers)]
[ApiController]
[Route("api/[controller]")]
public class ReportsController : ControllerBase
{
    private readonly AppDbContext _db;

    public ReportsController(AppDbContext db)
    {
        _db = db;
    }

    [HttpGet("summary")]
    public async Task<IActionResult> Summary()
    {
        var today = DateTime.UtcNow.Date;

        // InvoiceRules.ReportableStatuses (Approved, Partial, Paid) aur excluded nahi.
        // Statuses seedhe likhe hain taake SQL Server aur InMemory dono par saada OR bane.
        var reportable = _db.Invoices.Where(i =>
            !i.IsExcluded &&
            (i.Status == InvoiceStatus.Approved ||
             i.Status == InvoiceStatus.Partial ||
             i.Status == InvoiceStatus.Paid));

        var invoices = await reportable
            .Select(i => new { i.Id, i.Currency, i.Total })
            .ToListAsync();

        var overdueIds = (await _db.Invoices
                .Where(InvoiceRules.IsOverdue(today))
                .Select(i => i.Id)
                .ToListAsync())
            .ToHashSet();

        var paidByInvoice = await PaidByInvoiceAsync(reportable);

        // Har currency ki alag line: mukhtalif currencies kabhi aapas me jama nahi hotin
        var currencies = invoices
            .Select(i =>
            {
                var paid = PaidFor(paidByInvoice, i.Id);
                return new
                {
                    Currency = NormalizeCurrency(i.Currency),
                    i.Total,
                    Paid = paid,
                    Overdue = overdueIds.Contains(i.Id)
                };
            })
            .GroupBy(r => r.Currency)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                var totalInvoiced = g.Sum(r => r.Total);
                var totalPaid = g.Sum(r => r.Paid);
                return new
                {
                    currency = g.Key,
                    invoiceCount = g.Count(),
                    totalInvoiced,
                    totalPaid,
                    totalReceivable = totalInvoiced - totalPaid,
                    overdueCount = g.Count(r => r.Overdue),
                    overdueAmount = g.Where(r => r.Overdue).Sum(r => r.Total - r.Paid)
                };
            })
            .ToList();

        // Purane clients ke liye top-level totals sirf tab jab zyada se zyada ek currency ho;
        // kai currencies hon to null, kyunke mukhtalif currencies jama nahi ki ja saktin
        var singleCurrency = currencies.Count <= 1;

        return Ok(new
        {
            currencies,
            totalInvoiced = singleCurrency ? currencies.Sum(c => c.totalInvoiced) : (decimal?)null,
            totalPaid = singleCurrency ? currencies.Sum(c => c.totalPaid) : (decimal?)null,
            totalReceivable = singleCurrency ? currencies.Sum(c => c.totalReceivable) : (decimal?)null,
            overdueCount = currencies.Sum(c => c.overdueCount),
            generatedAtUtc = DateTime.UtcNow
        });
    }

    [HttpGet("overdue")]
    public async Task<IActionResult> Overdue()
    {
        var today = DateTime.UtcNow.Date;
        var overdue = _db.Invoices.Where(InvoiceRules.IsOverdue(today));

        var invoices = await overdue
            .OrderBy(i => i.DueDate)
            .ThenBy(i => i.Id)
            .Select(i => new
            {
                i.Id,
                i.InvoiceNumber,
                CustomerName = i.Customer!.Name,
                i.Currency,
                i.DueDate,
                i.Total,
                i.Status
            })
            .ToListAsync();

        var paidByInvoice = await PaidByInvoiceAsync(overdue);

        var list = invoices
            .Select(i =>
            {
                var paid = PaidFor(paidByInvoice, i.Id);
                return new
                {
                    id = i.Id,
                    invoiceNumber = i.InvoiceNumber,
                    customer = i.CustomerName,
                    // Summary jaisi currency key, taake dono reports milti julti rahen
                    currency = NormalizeCurrency(i.Currency),
                    dueDate = i.DueDate,
                    // amount = poora total (purane clients ke liye); baqi raqam "balance" me
                    amount = i.Total,
                    paid,
                    balance = i.Total - paid,
                    status = i.Status,
                    daysOverdue = (today - i.DueDate.Date).Days
                };
            })
            .ToList();

        return Ok(list);
    }

    // Di gayi invoices par kul jama raqam, InvoiceId ke hisab se (refund ki manfi entries shamil).
    // Grouping database me hoti hai, memory me sirf har invoice ki ek line aati hai.
    private async Task<Dictionary<int, decimal>> PaidByInvoiceAsync(IQueryable<Invoice> invoices)
    {
        var sums = await _db.Payments
            .Join(invoices, p => p.InvoiceId, i => i.Id, (p, i) => p)
            .GroupBy(p => p.InvoiceId)
            .Select(g => new { InvoiceId = g.Key, Paid = g.Sum(p => p.Amount) })
            .ToListAsync();

        return sums.ToDictionary(x => x.InvoiceId, x => x.Paid);
    }

    private static decimal PaidFor(Dictionary<int, decimal> paidByInvoice, int invoiceId) =>
        paidByInvoice.TryGetValue(invoiceId, out var paid) ? paid : 0m;

    private static string NormalizeCurrency(string? currency) =>
        string.IsNullOrWhiteSpace(currency) ? "N/A" : currency.Trim().ToUpperInvariant();
}
