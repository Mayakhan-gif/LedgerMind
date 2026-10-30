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
public class PaymentsController : ControllerBase
{
    private const string DefaultMethod = "Cash";
    private const string ReviewPendingMessage = "Is invoice ki online payment review me hai, pehle usay nipta lein.";
    private const string DuplicateMessage =
        "Yehi payment abhi abhi darj hui hai. Dobara darj karni ho to ek minute baad koshish karein.";

    // Isi waqfe me bilkul wahi payment dobara aaye to double submit samjha jata hai
    private static readonly TimeSpan DuplicateWindow = TimeSpan.FromSeconds(60);

    private readonly AppDbContext _db;
    private readonly PaymentLedger _ledger;
    private readonly AuditService _audit;
    private readonly IStripeGateway _stripe;
    private readonly ILogger<PaymentsController> _logger;

    public PaymentsController(
        AppDbContext db,
        PaymentLedger ledger,
        AuditService audit,
        IStripeGateway stripe,
        ILogger<PaymentsController> logger)
    {
        _db = db;
        _ledger = ledger;
        _audit = audit;
        _stripe = stripe;
        _logger = logger;
    }

    [HttpGet("invoice/{invoiceId}")]
    public async Task<IActionResult> GetByInvoice(int invoiceId)
    {
        var invoice = await _db.Invoices.FindAsync(invoiceId);
        if (invoice == null)
            return NotFound(new { message = "Invoice nahi mila." });

        var payments = await _db.Payments
            .Where(p => p.InvoiceId == invoiceId)
            .OrderBy(p => p.PaymentDate)
            .ThenBy(p => p.Id)
            .ToListAsync();

        // Refund ki reversal entries manfi hain, is liye seedha jama karne se sahi baqaya aata hai
        var totalPaid = payments.Sum(p => p.Amount);

        return Ok(new
        {
            invoiceId = invoice.Id,
            invoiceNumber = invoice.InvoiceNumber,
            currency = invoice.Currency,
            invoiceAmount = invoice.Total,
            totalPaid,
            balance = invoice.Total - totalPaid,
            status = invoice.Status,
            isExcluded = invoice.IsExcluded,
            payments
        });
    }

    [HttpPost]
    public async Task<IActionResult> Create(PaymentRequest request)
    {
        var invoice = await _db.Invoices.FindAsync(request.InvoiceId);
        if (invoice == null)
            return BadRequest(new { message = "Is InvoiceId ka invoice maujood nahi." });

        if (invoice.IsExcluded)
            return BadRequest(new { message = "Excluded invoice par payment nahi ho sakti." });

        if (!InvoiceRules.IsCollectable(invoice))
            return BadRequest(new { message = "Payment sirf Approved ya Partial invoice par ho sakti hai." });

        // Stripe paisa le chuka magar ledger me darj nahi (NeedsReview): pehle uska faisla ho,
        // warna customer se ek hi raqam do dafa li ja sakti hai (checkout bhi isi wajah se band hai)
        var hasUnresolvedOnlinePayment = await _db.PaymentAttempts.AnyAsync(a =>
            a.InvoiceId == invoice.Id && a.Status == PaymentAttemptStatus.NeedsReview);
        if (hasUnresolvedOnlinePayment)
            return BadRequest(new { message = ReviewPendingMessage });

        var paidBefore = await _ledger.GetPaidAsync(invoice.Id);
        var balance = invoice.Total - paidBefore;

        if (request.Amount > balance)
            return BadRequest(new
            {
                message = FormattableString.Invariant(
                    $"Payment baqaya se zyada hai. Baqaya: {invoice.Currency} {balance:N2}"),
                balance,
                currency = invoice.Currency
            });

        var method = string.IsNullOrWhiteSpace(request.Method) ? DefaultMethod : request.Method.Trim();
        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        // Double submit (button do dafa): isi user ki bilkul wahi payment abhi abhi darj hui ho to dobara nahi
        var since = DateTime.UtcNow - DuplicateWindow;
        var isDuplicate = await _db.Payments.AnyAsync(p =>
            p.InvoiceId == invoice.Id &&
            p.Amount == request.Amount &&
            p.PaymentDate == request.PaymentDate &&
            p.Method == method &&
            p.Note == note &&
            p.CreatedByUserId == userId &&
            p.CreatedAtUtc >= since);
        if (isDuplicate)
            return Conflict(new { message = DuplicateMessage });

        // Khule Stripe checkout band: unki raqam purana baqaya hai, un par ab payment zyada ya dobara hogi
        var closedCheckouts = await ExpireOpenCheckoutsAsync(invoice.Id);

        var payment = _ledger.StagePayment(
            invoice, paidBefore, request.Amount, request.PaymentDate, method, note, null, userId);

        var details = FormattableString.Invariant(
            $"{invoice.InvoiceNumber}: {invoice.Currency} {request.Amount:N2} ({method}), status {invoice.Status}");
        if (closedCheckouts > 0)
            details += $" ({closedCheckouts} Stripe checkout band)";

        // Payment, invoice ka naya status, band checkouts aur audit ek hi SaveChanges me
        _audit.Stage("PaymentRecorded", "Invoice", invoice.Id.ToString(), details);

        await _db.SaveChangesAsync();

        return Ok(payment);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        var payment = await _db.Payments.FindAsync(id);
        if (payment == null)
            return NotFound(new { message = "Payment nahi mili." });

        // Stripe ka paisa aur uski reversal sirf Stripe/refund ke raste se badalte hain
        if (payment.PaymentAttemptId != null || payment.Amount < 0)
            return BadRequest(new
            {
                message = "Stripe payment ya refund yahan se delete nahi hoti, refund ka rasta use karein."
            });

        var invoice = await _db.Invoices.FindAsync(payment.InvoiceId);
        if (invoice == null || !HasPaymentStatus(invoice.Status))
            return BadRequest(new
            {
                message = "Payment sirf Approved, Partial ya Paid invoice se delete ho sakti hai."
            });

        var paidBefore = await _ledger.GetPaidAsync(invoice.Id);

        _db.Payments.Remove(payment);
        _ledger.ApplyStatus(invoice, paidBefore - payment.Amount);

        _audit.Stage("PaymentDeleted", "Invoice", invoice.Id.ToString(),
            FormattableString.Invariant(
                $"{invoice.InvoiceNumber}: payment #{payment.Id} {invoice.Currency} {payment.Amount:N2} ({payment.Method}) delete, status {invoice.Status}"));

        await _db.SaveChangesAsync();

        return NoContent();
    }

    private static bool HasPaymentStatus(string status) =>
        status is InvoiceStatus.Approved or InvoiceStatus.Partial or InvoiceStatus.Paid;

    // Invoice ke khule (Pending) Stripe checkout Stripe par aur hamare record me band. Sirf stage, save caller karta hai.
    private async Task<int> ExpireOpenCheckoutsAsync(int invoiceId)
    {
        var openAttempts = await _db.PaymentAttempts
            .Where(a => a.InvoiceId == invoiceId && a.Status == PaymentAttemptStatus.Pending)
            .ToListAsync();

        var now = DateTime.UtcNow;
        foreach (var attempt in openAttempts)
        {
            try
            {
                await _stripe.ExpireCheckoutSessionAsync(attempt.ProviderSessionId);
            }
            catch (Exception ex) when (ex is global::Stripe.StripeException or InvalidOperationException)
            {
                // Aksar session pehle hi band hota hai (ya Stripe ki key set nahi). Phir bhi payment hui
                // to webhook usay NeedsReview karega. Network ki ghalti par kuch save nahi hota (500).
                _logger.LogWarning(ex, "Stripe session {SessionId} band nahi ho saka", attempt.ProviderSessionId);
            }

            attempt.Status = PaymentAttemptStatus.Expired;
            attempt.FailureReason = "Manual payment darj hui, checkout band.";
            attempt.CompletedAtUtc = now;
        }

        return openAttempts.Count;
    }
}
