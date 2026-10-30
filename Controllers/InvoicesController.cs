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
public class InvoicesController : ControllerBase
{
    private const string EntityType = "Invoice";
    private const string OwnWorkMessage = "Apna banaya ya submit kiya invoice khud approve/reject nahi kar sakte.";
    private const string EditedWorkMessage = "Jis invoice ko aap ne edit kiya, usay khud approve/reject nahi kar sakte.";

    // Jin statuses se invoice cancel ho sakta hai (aur sirf jab koi payment na ho)
    private static readonly string[] CancellableStatuses =
    {
        InvoiceStatus.Draft,
        InvoiceStatus.PendingApproval,
        InvoiceStatus.Approved,
        InvoiceStatus.Rejected
    };

    private readonly AppDbContext _db;
    private readonly AuditService _audit;
    private readonly IStripeGateway _stripe;
    private readonly ILogger<InvoicesController> _logger;

    public InvoicesController(
        AppDbContext db,
        AuditService audit,
        IStripeGateway stripe,
        ILogger<InvoicesController> logger)
    {
        _db = db;
        _audit = audit;
        _stripe = stripe;
        _logger = logger;
    }

    private string? CurrentUserId => User.FindFirstValue(ClaimTypes.NameIdentifier);
    private bool IsManager => User.IsInRole(Roles.Admin) || User.IsInRole(Roles.FinanceManager);

    // Har signed-in user sab invoices dekh sakta hai (business ka faisla: Employee bhi invoices dekhe aur
    // draft banaye). Badalna, approve, cancel aur delete ke qaide neeche alag hain.
    [HttpGet]
    public async Task<ActionResult<List<Invoice>>> GetAll()
    {
        return await _db.Invoices
            .Include(i => i.Customer)
            .OrderByDescending(i => i.Id)
            .ToListAsync();
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<Invoice>> GetById(int id)
    {
        var invoice = await _db.Invoices
            .Include(i => i.Customer)
            .FirstOrDefaultAsync(i => i.Id == id);

        if (invoice == null) return NotFound();
        return invoice;
    }

    [HttpPost]
    public async Task<ActionResult<Invoice>> Create(InvoiceRequest request)
    {
        var number = request.InvoiceNumber.Trim();

        var error = await ValidateReferencesAsync(request.CustomerId, number, excludeInvoiceId: 0);
        if (error != null)
            return BadRequest(new { message = error });

        var userId = CurrentUserId;

        // Id save ke baad milti hai: invoice aur uska audit ek hi transaction me (ya dono, ya koi nahi).
        // Execution strategy ke andar, taake SQL Server retry on hone par bhi user transaction chale.
        var strategy = _db.Database.CreateExecutionStrategy();
        var invoice = await strategy.ExecuteAsync(async () =>
        {
            // Retry par pichli adhoori koshish ka kuch bhi context me na rahe
            _db.ChangeTracker.Clear();

            var created = new Invoice
            {
                InvoiceNumber = number,
                CustomerId = request.CustomerId,
                Description = request.Description,
                InvoiceDate = request.InvoiceDate,
                DueDate = request.DueDate,
                Currency = request.Currency.Trim().ToUpperInvariant(),
                Subtotal = request.Subtotal,
                TaxAmount = request.TaxAmount,
                Total = request.Subtotal + request.TaxAmount,
                Status = InvoiceStatus.Draft,
                IsExcluded = request.IsExcluded,
                CreatedByUserId = userId,
                CreatedAtUtc = DateTime.UtcNow
            };

            await using var transaction = await _db.Database.BeginTransactionAsync();

            _db.Invoices.Add(created);
            await _db.SaveChangesAsync();

            _audit.Stage("InvoiceCreated", EntityType, created.Id.ToString(), Summary(created));
            await _db.SaveChangesAsync();

            await transaction.CommitAsync();
            return created;
        });

        return CreatedAtAction(nameof(GetById), new { id = invoice.Id }, invoice);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, InvoiceRequest request)
    {
        var invoice = await _db.Invoices.FindAsync(id);
        if (invoice == null) return NotFound();

        if (!CanModify(invoice)) return Forbid();

        if (invoice.Status != InvoiceStatus.Draft)
            return BadRequest(new { message = "Sirf Draft invoice edit ho sakta hai." });

        var number = request.InvoiceNumber.Trim();

        var error = await ValidateReferencesAsync(request.CustomerId, number, excludeInvoiceId: id);
        if (error != null)
            return BadRequest(new { message = error });

        invoice.InvoiceNumber = number;
        invoice.CustomerId = request.CustomerId;
        invoice.Description = request.Description;
        invoice.InvoiceDate = request.InvoiceDate;
        invoice.DueDate = request.DueDate;
        invoice.Currency = request.Currency.Trim().ToUpperInvariant();
        invoice.Subtotal = request.Subtotal;
        invoice.TaxAmount = request.TaxAmount;
        invoice.Total = request.Subtotal + request.TaxAmount;
        invoice.IsExcluded = request.IsExcluded;

        // Is audit row se editor bhi "author" ginta hai (InvoiceAuthorship): wo khud approve nahi kar sakta
        _audit.Stage(InvoiceAuthorship.UpdatedAction, EntityType, invoice.Id.ToString(), Summary(invoice));
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("{id}/submit")]
    public async Task<IActionResult> Submit(int id)
    {
        var invoice = await _db.Invoices.FindAsync(id);
        if (invoice == null) return NotFound();

        if (!CanModify(invoice)) return Forbid();

        if (invoice.Status != InvoiceStatus.Draft)
            return BadRequest(new { message = "Sirf Draft invoice submit ho sakta hai." });

        invoice.Status = InvoiceStatus.PendingApproval;
        invoice.SubmittedByUserId = CurrentUserId;

        _audit.Stage("InvoiceSubmitted", EntityType, invoice.Id.ToString(), invoice.InvoiceNumber);
        await _db.SaveChangesAsync();
        return Ok(invoice);
    }

    // Body optional: React client baghair body ke bhejta hai, { comment } bhi chalta hai
    [Authorize(Roles = Roles.Managers)]
    [HttpPost("{id}/approve")]
    public async Task<IActionResult> Approve(
        int id,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] ReviewRequest? request)
    {
        return await ReviewAsync(id, request, InvoiceStatus.Approved, "InvoiceApproved",
            "Sirf PendingApproval invoice approve ho sakta hai.");
    }

    [Authorize(Roles = Roles.Managers)]
    [HttpPost("{id}/reject")]
    public async Task<IActionResult> Reject(
        int id,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] ReviewRequest? request)
    {
        return await ReviewAsync(id, request, InvoiceStatus.Rejected, "InvoiceRejected",
            "Sirf PendingApproval invoice reject ho sakta hai.");
    }

    // Rejected invoice dobara Draft: theek karke phir submit ho sakta hai
    [HttpPost("{id}/reopen")]
    public async Task<IActionResult> Reopen(int id)
    {
        var invoice = await _db.Invoices.FindAsync(id);
        if (invoice == null) return NotFound();

        if (!CanModify(invoice)) return Forbid();

        if (invoice.Status != InvoiceStatus.Rejected)
            return BadRequest(new { message = "Sirf Rejected invoice dobara khul sakta hai." });

        invoice.Status = InvoiceStatus.Draft;
        invoice.SubmittedByUserId = null;
        invoice.ReviewedByUserId = null;
        invoice.ReviewedAtUtc = null;
        invoice.ReviewComment = null;

        _audit.Stage("InvoiceReopened", EntityType, invoice.Id.ToString(), invoice.InvoiceNumber);
        await _db.SaveChangesAsync();
        return Ok(invoice);
    }

    [Authorize(Roles = Roles.Managers)]
    [HttpPost("{id}/cancel")]
    public async Task<IActionResult> Cancel(
        int id,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] ReviewRequest? request)
    {
        var invoice = await _db.Invoices.FindAsync(id);
        if (invoice == null) return NotFound();

        if (!CancellableStatuses.Contains(invoice.Status))
            return BadRequest(new { message = "Sirf Draft, PendingApproval, Approved ya Rejected invoice cancel ho sakta hai." });

        // Paise ka record ho to cancel nahi (refund ki reversal entry bhi record hai)
        var hasPayments = await _db.Payments.AnyAsync(p => p.InvoiceId == id);
        if (hasPayments)
            return BadRequest(new { message = "Is invoice par payments maujood hain, cancel nahi ho sakta." });

        // Stripe paisa le chuka magar ledger me darj nahi (NeedsReview): pehle uska faisla ho
        var unresolvedOnlinePayment = await _db.PaymentAttempts.AnyAsync(a =>
            a.InvoiceId == id && a.Status == PaymentAttemptStatus.NeedsReview);
        if (unresolvedOnlinePayment)
            return BadRequest(new { message = "Is invoice ki online payment review me hai, pehle usay nipta lein." });

        // Khule Stripe checkout band: cancel ke baad un par payment na ho sake
        var openAttempts = await _db.PaymentAttempts
            .Where(a => a.InvoiceId == id && a.Status == PaymentAttemptStatus.Pending)
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
                // Aksar session pehle hi band hota hai (ya Stripe ki key ab set nahi). Phir bhi payment hui
                // to webhook usay NeedsReview karega. Network ki ghalti par kuch save nahi hota (500).
                _logger.LogWarning(ex, "Stripe session {SessionId} band nahi ho saka", attempt.ProviderSessionId);
            }

            attempt.Status = PaymentAttemptStatus.Expired;
            attempt.FailureReason = "Invoice cancel hua, checkout band.";
            attempt.CompletedAtUtc = now;
        }

        var comment = CleanComment(request);

        // Reviewed* fields wese hi rehte hain (kis ne approve/reject kiya). Cancel karne wala audit me hai.
        invoice.Status = InvoiceStatus.Cancelled;
        if (comment != null)
            invoice.ReviewComment = comment;

        var details = openAttempts.Count == 0
            ? invoice.InvoiceNumber
            : $"{invoice.InvoiceNumber} ({openAttempts.Count} Stripe checkout band)";

        _audit.Stage("InvoiceCancelled", EntityType, invoice.Id.ToString(), WithComment(details, comment));
        await _db.SaveChangesAsync();
        return Ok(invoice);
    }

    [Authorize(Roles = Roles.Managers)]
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        var invoice = await _db.Invoices.FindAsync(id);
        if (invoice == null) return NotFound();

        if (invoice.Status != InvoiceStatus.Draft)
            return BadRequest(new { message = "Sirf Draft invoice delete ho sakta hai." });

        _db.Invoices.Remove(invoice);
        _audit.Stage("InvoiceDeleted", EntityType, invoice.Id.ToString(), Summary(invoice));
        await _db.SaveChangesAsync();
        return NoContent();
    }

    private async Task<IActionResult> ReviewAsync(
        int id, ReviewRequest? request, string newStatus, string auditAction, string wrongStatusMessage)
    {
        var invoice = await _db.Invoices.FindAsync(id);
        if (invoice == null) return NotFound();

        if (invoice.Status != InvoiceStatus.PendingApproval)
            return BadRequest(new { message = wrongStatusMessage });

        if (IsOwnWork(invoice))
            return BadRequest(new { message = OwnWorkMessage });

        // Kisi aur ka Draft badal kar khud approve karna bhi apna kaam approve karna hai
        var userId = CurrentUserId;
        if (userId != null && await InvoiceAuthorship.HasEditedAsync(_db, invoice.Id, userId))
            return BadRequest(new { message = EditedWorkMessage });

        var comment = CleanComment(request);

        invoice.Status = newStatus;
        invoice.ReviewedByUserId = userId;
        invoice.ReviewedAtUtc = DateTime.UtcNow;
        invoice.ReviewComment = comment;

        _audit.Stage(auditAction, EntityType, invoice.Id.ToString(),
            WithComment(invoice.InvoiceNumber, comment));
        await _db.SaveChangesAsync();
        return Ok(invoice);
    }

    // Customer maujood ho aur number kisi aur invoice ka na ho (excludeInvoiceId = 0 naye invoice ke liye)
    private async Task<string?> ValidateReferencesAsync(int customerId, string invoiceNumber, int excludeInvoiceId)
    {
        var customerExists = await _db.Customers.AnyAsync(c => c.Id == customerId);
        if (!customerExists)
            return "Is CustomerId ka customer maujood nahi.";

        var numberTaken = await _db.Invoices
            .AnyAsync(i => i.InvoiceNumber == invoiceNumber && i.Id != excludeInvoiceId);
        if (numberTaken)
            return "Ye InvoiceNumber pehle se maujood hai.";

        return null;
    }

    // Banane wala ya manager. Purane invoices (creator null) sirf manager badal sakta hai.
    private bool CanModify(Invoice invoice)
    {
        if (IsManager) return true;

        var userId = CurrentUserId;
        return userId != null && invoice.CreatedByUserId == userId;
    }

    // Dekhne ka qaida bhi wahi: banane wala ya manager (purane invoices sirf manager)
    // Segregation of duties: banane ya submit karne wala khud faisla nahi karta
    private bool IsOwnWork(Invoice invoice)
    {
        var userId = CurrentUserId;
        return userId != null &&
               (invoice.CreatedByUserId == userId || invoice.SubmittedByUserId == userId);
    }

    private static string? CleanComment(ReviewRequest? request)
    {
        var comment = request?.Comment?.Trim();
        return string.IsNullOrEmpty(comment) ? null : comment;
    }

    private static string WithComment(string text, string? comment) =>
        comment == null ? text : $"{text}: {comment}";

    private static string Summary(Invoice invoice) =>
        $"{invoice.InvoiceNumber}, {invoice.Currency} {invoice.Total.ToString("0.00", CultureInfo.InvariantCulture)}";
}
