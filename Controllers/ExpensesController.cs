using System.Globalization;
using System.Security.Claims;
using LedgerMind.Data;
using LedgerMind.Models;
using LedgerMind.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;

namespace LedgerMind.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class ExpensesController : ControllerBase
{
    private const string AuditEntity = "Expense";

    private readonly AppDbContext _db;
    private readonly AuditService _audit;

    public ExpensesController(AppDbContext db, AuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    private string CurrentUserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    private bool IsReviewer => User.IsInRole(Roles.Admin) || User.IsInRole(Roles.FinanceManager);

    // Employee sirf apne expenses dekhta hai, reviewer sab dekhta hai
    [HttpGet]
    public async Task<ActionResult<List<Expense>>> GetAll()
    {
        var query = _db.Expenses.AsQueryable();
        if (!IsReviewer)
            query = query.Where(e => e.SubmittedByUserId == CurrentUserId);

        return await query.OrderByDescending(e => e.Id).ToListAsync();
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<Expense>> GetById(int id)
    {
        var expense = await _db.Expenses.FindAsync(id);
        if (expense == null) return NotFound();

        if (!IsReviewer && expense.SubmittedByUserId != CurrentUserId)
            return NotFound();

        return expense;
    }

    [HttpPost]
    public async Task<ActionResult<Expense>> Create(ExpenseRequest request)
    {
        var expense = new Expense
        {
            Title = request.Title,
            Category = request.Category,
            Amount = request.Amount,
            Currency = request.Currency.Trim().ToUpperInvariant(),
            ExpenseDate = request.ExpenseDate,
            Description = request.Description,
            SubmittedByUserId = CurrentUserId
        };

        // Naye expense ka Id save ke baad milta hai, is liye expense aur audit
        // ek hi transaction me jate hain (ya dono, ya koi nahi)
        await using var transaction = await _db.Database.BeginTransactionAsync();

        _db.Expenses.Add(expense);
        await _db.SaveChangesAsync();

        _audit.Stage("ExpenseCreated", AuditEntity, expense.Id.ToString(), Describe(expense));
        await _db.SaveChangesAsync();

        await transaction.CommitAsync();

        return CreatedAtAction(nameof(GetById), new { id = expense.Id }, expense);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, ExpenseRequest request)
    {
        var expense = await _db.Expenses.FindAsync(id);
        if (expense == null || expense.SubmittedByUserId != CurrentUserId)
            return NotFound();

        if (expense.Status != ExpenseStatus.Draft)
            return BadRequest(new { message = "Sirf Draft expense edit ho sakta hai." });

        expense.Title = request.Title;
        expense.Category = request.Category;
        expense.Amount = request.Amount;
        expense.Currency = request.Currency.Trim().ToUpperInvariant();
        expense.ExpenseDate = request.ExpenseDate;
        expense.Description = request.Description;

        _audit.Stage("ExpenseUpdated", AuditEntity, expense.Id.ToString(), Describe(expense));
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("{id}/submit")]
    public async Task<IActionResult> Submit(int id)
    {
        var expense = await _db.Expenses.FindAsync(id);
        if (expense == null || expense.SubmittedByUserId != CurrentUserId)
            return NotFound();

        if (expense.Status != ExpenseStatus.Draft)
            return BadRequest(new { message = "Sirf Draft expense submit ho sakta hai." });

        expense.Status = ExpenseStatus.Submitted;

        _audit.Stage("ExpenseSubmitted", AuditEntity, expense.Id.ToString(), Describe(expense));
        await _db.SaveChangesAsync();
        return Ok(expense);
    }

    // Body optional hai: comment ke baghair bhi approve/reject ho sakta hai
    [Authorize(Roles = Roles.Managers)]
    [HttpPost("{id}/approve")]
    public Task<IActionResult> Approve(
        int id,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] ReviewRequest? request)
    {
        return Review(id, request?.Comment, ExpenseStatus.Approved);
    }

    [Authorize(Roles = Roles.Managers)]
    [HttpPost("{id}/reject")]
    public Task<IActionResult> Reject(
        int id,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] ReviewRequest? request)
    {
        return Review(id, request?.Comment, ExpenseStatus.Rejected);
    }

    private async Task<IActionResult> Review(int id, string? comment, string newStatus)
    {
        var expense = await _db.Expenses.FindAsync(id);
        if (expense == null) return NotFound();

        if (expense.Status != ExpenseStatus.Submitted)
            return BadRequest(new { message = "Sirf Submitted expense review ho sakta hai." });

        // Segregation of duties: apna expense khud review nahi
        if (expense.SubmittedByUserId == CurrentUserId)
            return BadRequest(new { message = "Apna expense khud approve ya reject nahi kar sakte." });

        var cleanComment = comment?.Trim();
        if (string.IsNullOrEmpty(cleanComment))
            cleanComment = null;

        expense.Status = newStatus;
        expense.ReviewedByUserId = CurrentUserId;
        expense.ReviewComment = cleanComment;
        expense.ReviewedAtUtc = DateTime.UtcNow;

        var action = newStatus == ExpenseStatus.Approved ? "ExpenseApproved" : "ExpenseRejected";
        _audit.Stage(action, AuditEntity, expense.Id.ToString(), cleanComment);
        await _db.SaveChangesAsync();

        return Ok(expense);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        var expense = await _db.Expenses.FindAsync(id);
        if (expense == null || expense.SubmittedByUserId != CurrentUserId)
            return NotFound();

        if (expense.Status != ExpenseStatus.Draft)
            return BadRequest(new { message = "Sirf Draft expense delete ho sakta hai." });

        _audit.Stage("ExpenseDeleted", AuditEntity, expense.Id.ToString(), Describe(expense));
        _db.Expenses.Remove(expense);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // Audit ke liye mukhtasar khulasa
    private static string Describe(Expense expense) =>
        $"{expense.Title} | {expense.Category} | {expense.Currency} " +
        expense.Amount.ToString("0.00", CultureInfo.InvariantCulture);
}
