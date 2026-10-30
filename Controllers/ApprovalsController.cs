using System.Security.Claims;
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
public class ApprovalsController : ControllerBase
{
    private readonly AppDbContext _db;

    public ApprovalsController(AppDbContext db)
    {
        _db = db;
    }

    [HttpGet("pending")]
    public async Task<IActionResult> Pending()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;

        // Jo invoices is user ne (Draft me) edit kiye, wo bhi uske apne kaam hain
        var editedIds = await InvoiceAuthorship.EditedInvoiceIdsAsync(_db, userId);

        // Segregation of duties: apna banaya, submit ya edit kiya invoice queue me nahi aata
        var invoices = await _db.Invoices
            .Where(i => i.Status == InvoiceStatus.PendingApproval &&
                        (i.CreatedByUserId == null || i.CreatedByUserId != userId) &&
                        (i.SubmittedByUserId == null || i.SubmittedByUserId != userId) &&
                        !editedIds.Contains(i.Id))
            .OrderBy(i => i.InvoiceDate)
            .ThenBy(i => i.Id)
            .Select(i => new
            {
                i.Id,
                i.InvoiceNumber,
                customer = i.Customer!.Name,
                i.Currency,
                i.Total,
                i.DueDate
            })
            .ToListAsync();

        // Apna expense bhi nahi
        var expenses = await _db.Expenses
            .Where(e => e.Status == ExpenseStatus.Submitted && e.SubmittedByUserId != userId)
            .OrderBy(e => e.ExpenseDate)
            .ThenBy(e => e.Id)
            .Select(e => new
            {
                e.Id,
                e.Title,
                e.Category,
                e.Currency,
                e.Amount,
                e.ExpenseDate
            })
            .ToListAsync();

        return Ok(new
        {
            invoiceCount = invoices.Count,
            expenseCount = expenses.Count,
            invoices,
            expenses
        });
    }
}
