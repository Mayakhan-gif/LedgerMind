using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Net;
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
public class EmailController : ControllerBase
{
    public const string InvoiceReminderType = "InvoiceReminder";
    private const string SentStatus = "Sent";

    // Ek invoice ka kamyab reminder 24 ghante me ek hi dafa
    private static readonly TimeSpan ReminderThrottle = TimeSpan.FromHours(24);

    // Ek hi invoice ki do requests ek saath 24h check paar karke do emails na bhej dein:
    // check + send ek waqt me ek request (invoice id se lock chuna jata hai, memory mehdood).
    // Sirf isi process ke andar; kai servers hon to DB level pabandi chahiye hogi.
    private static readonly SemaphoreSlim[] ReminderLocks =
        Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    private readonly AppDbContext _db;
    private readonly EmailService _email;
    private readonly AuditService _audit;
    private readonly PaymentLedger _ledger;

    public EmailController(AppDbContext db, EmailService email, AuditService audit, PaymentLedger ledger)
    {
        _db = db;
        _email = email;
        _audit = audit;
        _ledger = ledger;
    }

    [HttpPost("invoice-reminder/{invoiceId}")]
    public async Task<IActionResult> SendInvoiceReminder(int invoiceId)
    {
        var invoice = await _db.Invoices
            .Include(i => i.Customer)
            .FirstOrDefaultAsync(i => i.Id == invoiceId);

        if (invoice == null)
            return NotFound(new { message = "Invoice nahi mili." });

        // Excluded, Draft, PendingApproval, Rejected, Paid, Cancelled: reminder nahi
        if (!InvoiceRules.IsCollectable(invoice))
            return BadRequest(new { message = "Reminder sirf Approved ya Partial invoice ka ja sakta hai." });

        var customerEmail = invoice.Customer?.Email?.Trim();
        if (invoice.Customer == null || string.IsNullOrWhiteSpace(customerEmail))
            return BadRequest(new { message = "Is customer ki email maujood nahi." });

        if (!new EmailAddressAttribute().IsValid(customerEmail))
            return BadRequest(new { message = "Is customer ki email sahih nahi." });

        var paid = await _ledger.GetPaidAsync(invoice.Id);
        var balance = invoice.Total - paid;
        if (balance <= 0)
            return BadRequest(new { message = "Is invoice par koi baqaya nahi." });

        var entityId = invoice.Id.ToString(CultureInfo.InvariantCulture);

        var gate = ReminderLocks[(int)((uint)invoice.Id % (uint)ReminderLocks.Length)];
        await gate.WaitAsync(HttpContext.RequestAborted);
        try
        {
            var since = DateTime.UtcNow - ReminderThrottle;

            // Sirf kamyab (Sent) reminders gine jate hain; fail hone ke baad dobara bhej sakte hain
            var sentRecently = await _db.EmailLogs.AnyAsync(l =>
                l.Type == InvoiceReminderType &&
                l.EntityId == entityId &&
                l.Status == SentStatus &&
                l.CreatedAtUtc > since);

            if (sentRecently)
                return BadRequest(new { message = "Is invoice ka reminder pichle 24 ghante me ja chuka hai." });

            // Customer ka naam/invoice number HTML me encode (email me HTML inject na ho)
            var name = WebUtility.HtmlEncode(invoice.Customer.Name);
            var number = WebUtility.HtmlEncode(invoice.InvoiceNumber);
            var currency = WebUtility.HtmlEncode(invoice.Currency);
            var due = invoice.DueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var totalText = invoice.Total.ToString("N2", CultureInfo.InvariantCulture);
            var balanceText = balance.ToString("N2", CultureInfo.InvariantCulture);

            var body = $"""
                <p>Dear {name},</p>
                <p>This is a friendly reminder about invoice <b>{number}</b>.</p>
                <ul>
                  <li>Due date: {due}</li>
                  <li>Invoice total: {currency} {totalText}</li>
                  <li>Outstanding balance: {currency} {balanceText}</li>
                </ul>
                <p>If you have already paid, please ignore this email.</p>
                <p>Regards,<br/>LedgerMind</p>
                """;

            // EmailService har koshish (Sent ya Failed) EmailLogs me likhta hai
            var sent = await _email.SendAsync(
                customerEmail,
                $"Payment reminder: invoice {invoice.InvoiceNumber}",
                body,
                InvoiceReminderType,
                "Invoice",
                entityId);

            if (!sent)
                return StatusCode(StatusCodes.Status502BadGateway,
                    new { message = "Email nahi ja saki. EmailLogs me wajah dekhein." });

            await _audit.LogAsync("InvoiceReminderSent", "Invoice", entityId,
                $"{invoice.InvoiceNumber}: {invoice.Currency} {balanceText} baqaya, {customerEmail} ko");

            return Ok(new
            {
                message = "Reminder email bhej di gayi.",
                sentTo = customerEmail,
                currency = invoice.Currency,
                balance
            });
        }
        finally
        {
            gate.Release();
        }
    }

    // Filters exact match: type (jaise InvoiceReminder), entityId (jaise invoice id)
    [Authorize(Roles = Roles.Admin)]
    [HttpGet("logs")]
    public async Task<ActionResult<List<EmailLog>>> Logs(
        [FromQuery] string? type = null,
        [FromQuery] string? entityId = null,
        [FromQuery] int take = 50)
    {
        take = Math.Clamp(take, 1, 200);

        var query = _db.EmailLogs.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(type))
            query = query.Where(l => l.Type == type);

        if (!string.IsNullOrWhiteSpace(entityId))
            query = query.Where(l => l.EntityId == entityId);

        return await query
            .OrderByDescending(l => l.Id)
            .Take(take)
            .ToListAsync();
    }
}
