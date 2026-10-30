using LedgerMind.Data;
using LedgerMind.Models;
using Microsoft.EntityFrameworkCore;
using CheckoutSession = Stripe.Checkout.Session;

namespace LedgerMind.Services;

// Checkout ka nateeja: ya link, ya 400 wala saaf message
public sealed record StripeCheckoutResult(
    string? CheckoutUrl,
    string? Error,
    int? AttemptId,
    decimal Amount,
    string? Currency,
    DateTime? ExpiresAtUtc)
{
    public static StripeCheckoutResult Fail(string error) => new(null, error, null, 0m, null, null);

    // Purane "(Url, Error)" tuple wale callers ke liye
    public string? Url => CheckoutUrl;

    public void Deconstruct(out string? url, out string? error)
    {
        url = CheckoutUrl;
        error = Error;
    }
}

public enum StripeAttemptResultKind
{
    Ok,
    NotFound,
    Invalid,
    ProviderFailed,
    Conflict
}

// Resolve/refund ka nateeja (controller isay 200/404/400/502/409 me badalta hai)
public sealed record StripeAttemptResult(
    StripeAttemptResultKind Kind,
    string? Message,
    StripeAttemptSummary? Attempt)
{
    public static StripeAttemptResult Success(StripeAttemptSummary attempt) =>
        new(StripeAttemptResultKind.Ok, null, attempt);

    public static StripeAttemptResult Missing(string message) =>
        new(StripeAttemptResultKind.NotFound, message, null);

    public static StripeAttemptResult Invalid(string message) =>
        new(StripeAttemptResultKind.Invalid, message, null);

    public static StripeAttemptResult ProviderFailed(string message) =>
        new(StripeAttemptResultKind.ProviderFailed, message, null);

    public static StripeAttemptResult Conflict(string message) =>
        new(StripeAttemptResultKind.Conflict, message, null);
}

public class StripePaymentService
{
    private const string Provider = "Stripe";
    private const string AuditEntity = "Invoice";
    private const int OutcomeLength = 300;
    private const int PaymentNoteLength = 300;
    private const int ReasonLength = 500;
    private const int MaxListSize = 200;

    // Stripe expires_at 30 minute se 24 ghante tak maanta hai. Seconds kat jate hain aur network me
    // waqt lagta hai, is liye dono kinaron se ek minute ki gunjaish.
    private const int DefaultExpiryMinutes = 60;
    private const int MinExpiryMinutes = 31;
    private const int MaxExpiryMinutes = 1439;

    private const string DefaultSuccessUrl = "http://localhost:5173/payment/success";
    private const string DefaultCancelUrl = "http://localhost:5173/payment/cancel";

    private const string UnmatchedSession = "Unmatched: hamare record me ye session nahi.";
    private const string IgnoredEvent = "Ignored: is event ki zaroorat nahi.";
    private const string ReviewPendingMessage = "Is invoice ki online payment review me hai, pehle usay nipta lein.";
    private const string RetryRefundMessage = "Retry: is refund ki payment abhi darj nahi hui, Stripe dobara bhejega.";

    // Stripe events ki tarteeb pakki nahi. Itne purane event tak refund ka retry maangte hain
    // (Stripe bhi takreeban 3 din tak hi dobara bhejta hai).
    private static readonly TimeSpan RefundRetryWindow = TimeSpan.FromDays(3);

    private static readonly string[] AllStatuses =
    {
        PaymentAttemptStatus.Pending,
        PaymentAttemptStatus.Succeeded,
        PaymentAttemptStatus.Failed,
        PaymentAttemptStatus.Expired,
        PaymentAttemptStatus.NeedsReview,
        PaymentAttemptStatus.Refunded,
        PaymentAttemptStatus.Resolved
    };

    private readonly IConfiguration _config;
    private readonly AppDbContext _db;
    private readonly AuditService _audit;
    private readonly PaymentLedger _ledger;
    private readonly IStripeGateway _gateway;
    private readonly ILogger<StripePaymentService> _logger;

    public StripePaymentService(
        IConfiguration config,
        AppDbContext db,
        AuditService audit,
        PaymentLedger ledger,
        IStripeGateway gateway,
        ILogger<StripePaymentService> logger)
    {
        _config = config;
        _db = db;
        _audit = audit;
        _ledger = ledger;
        _gateway = gateway;
        _logger = logger;
    }

    // ---------- 1. Checkout link banana ----------
    public async Task<StripeCheckoutResult> CreateCheckoutAsync(int invoiceId, string? userId)
    {
        // Setting na ho to Stripe ya database ko chhone se pehle hi saaf 500
        EnsureSecretKey();

        var invoice = await _db.Invoices.FirstOrDefaultAsync(i => i.Id == invoiceId);
        if (invoice == null)
            return StripeCheckoutResult.Fail("Invoice nahi mila.");

        if (!InvoiceRules.IsCollectable(invoice))
            return StripeCheckoutResult.Fail("Payment sirf Approved ya Partial invoice par ho sakti hai.");

        if (!Currencies.IsSupported(invoice.Currency))
            return StripeCheckoutResult.Fail(
                $"{invoice.Currency} currency me online payment nahi ho sakti. Ijazat: {string.Join(", ", Currencies.Supported)}.");

        // Stripe paisa le chuka magar ledger me darj nahi (NeedsReview): naya link customer se dobara paisa le sakta hai
        var hasUnresolvedPayment = await _db.PaymentAttempts.AnyAsync(a =>
            a.InvoiceId == invoice.Id && a.Status == PaymentAttemptStatus.NeedsReview);
        if (hasUnresolvedPayment)
            return StripeCheckoutResult.Fail(ReviewPendingMessage);

        var paid = await _ledger.GetPaidAsync(invoice.Id);
        var balance = invoice.Total - paid;
        if (balance <= 0)
            return StripeCheckoutResult.Fail("Is invoice ka koi baqaya nahi.");

        var currency = invoice.Currency.Trim().ToUpperInvariant();

        // Purane khule checkout band: ek invoice ka ek hi link chalta rahe
        var openAttempts = await _db.PaymentAttempts
            .Where(a => a.InvoiceId == invoice.Id && a.Status == PaymentAttemptStatus.Pending)
            .ToListAsync();

        foreach (var old in openAttempts)
        {
            try
            {
                await _gateway.ExpireCheckoutSessionAsync(old.ProviderSessionId);
            }
            catch (Stripe.StripeException ex)
            {
                // Aksar session pehle hi band ho chuka hota hai. Us par payment hui ho to webhook phir bhi darj karega.
                _logger.LogWarning(ex, "Purana Stripe session {SessionId} band nahi ho saka", old.ProviderSessionId);
            }

            old.Status = PaymentAttemptStatus.Expired;
            old.FailureReason = "Naya checkout bana, purana band.";
            old.CompletedAtUtc = DateTime.UtcNow;
        }

        // Stripe par band ho chuke, is liye naya session fail ho tab bhi record sahi rahe.
        // Isi waqt purane session ki payment ka webhook commit ho to invoice row par takraav (409),
        // taake Succeeded attempt par Expired na likha jaye.
        if (openAttempts.Count > 0)
        {
            GuardInvoice(invoice);
            await _db.SaveChangesAsync();
        }

        var expiryMinutes = Math.Clamp(
            _config.GetValue("Stripe:CheckoutExpiryMinutes", DefaultExpiryMinutes),
            MinExpiryMinutes,
            MaxExpiryMinutes);
        var expiresAtUtc = DateTime.UtcNow.AddMinutes(expiryMinutes);

        var metadata = new Dictionary<string, string>
        {
            ["invoiceId"] = invoice.Id.ToString(),
            ["invoiceNumber"] = invoice.InvoiceNumber
        };

        var session = await _gateway.CreateCheckoutSessionAsync(new StripeCheckoutRequest(
            currency,
            InvoiceRules.ToMinorUnits(balance),
            "Invoice " + invoice.InvoiceNumber,
            ConfigOrDefault("Stripe:SuccessUrl", DefaultSuccessUrl),
            ConfigOrDefault("Stripe:CancelUrl", DefaultCancelUrl),
            invoice.Id.ToString(),
            expiresAtUtc,
            metadata));

        var attempt = new PaymentAttempt
        {
            InvoiceId = invoice.Id,
            Provider = Provider,
            ProviderSessionId = session.Id,
            Amount = balance,
            Currency = currency,
            Status = PaymentAttemptStatus.Pending,
            CreatedByUserId = userId,
            CreatedAtUtc = DateTime.UtcNow
        };
        _db.PaymentAttempts.Add(attempt);

        // Hamesha invoice row par takraav ka pehra: isi invoice ka doosra checkout (ya payment) isi waqt
        // commit ho to ye save 409 paata hai. Do khule link nahi bante, aur baqaya purana nahi hota.
        // Haarne wali request ka Stripe session kisi ko nahi milta, wo khud expire ho jata hai.
        GuardInvoice(invoice);

        // Attempt aur audit ek hi SaveChanges me
        _audit.Stage("PaymentInitiated", AuditEntity, invoice.Id.ToString(),
            $"Stripe checkout, {currency} {balance:N2}, session {session.Id}");
        await _db.SaveChangesAsync();

        return new StripeCheckoutResult(session.Url, null, attempt.Id, balance, currency, expiresAtUtc);
    }

    // ---------- 2. Webhook sambhalna ----------
    public async Task<(int Status, string Message)> HandleWebhookAsync(string json, string? signature)
    {
        var webhookSecret = _config["Stripe:WebhookSecret"];
        if (string.IsNullOrWhiteSpace(webhookSecret))
            throw new StripeConfigurationException("Stripe:WebhookSecret set nahi hai.");

        if (string.IsNullOrWhiteSpace(signature))
            return (400, "Signature maujood nahi.");

        Stripe.Event stripeEvent;
        try
        {
            // Qadam 1: mohar check. Ghalat ho to StripeException aata hai.
            stripeEvent = Stripe.EventUtility.ConstructEvent(
                json, signature, webhookSecret, throwOnApiVersionMismatch: false);
        }
        catch (Stripe.StripeException ex)
        {
            _logger.LogWarning("Stripe webhook ki signature ghalat: {Message}", ex.Message);
            return (400, "Signature ghalat hai.");
        }

        // Qadam 2: duplicate rokna
        var record = await _db.PaymentWebhookEvents
            .FirstOrDefaultAsync(e => e.Provider == Provider && e.EventId == stripeEvent.Id);

        if (record != null && record.ProcessedAtUtc != null)
            return (200, "Ye event pehle process ho chuka hai.");

        if (record == null)
        {
            record = new PaymentWebhookEvent
            {
                Provider = Provider,
                EventId = stripeEvent.Id,
                EventType = stripeEvent.Type
            };
            _db.PaymentWebhookEvents.Add(record);

            try
            {
                await _db.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                // Usi waqt koi aur request wahi event la chuki hai. Stripe baad me dobara bhejega.
                return (409, "Event abhi process ho raha hai.");
            }
        }

        try
        {
            // Qadam 3: asal kaam sirf stage hota hai, phir record ke saath ek hi SaveChanges
            var outcome = await ProcessEventAsync(stripeEvent);
            record.Outcome = Truncate(outcome, OutcomeLength);
            record.ProcessedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            _logger.LogInformation("Stripe event {EventId} ({Type}): {Outcome}",
                stripeEvent.Id, stripeEvent.Type, outcome);
            return (200, outcome);
        }
        catch (WebhookRetryException ex)
        {
            // Abhi darj nahi ho sakta (e.g. refund apni payment se pehle aa gaya). Kuch save nahi hua,
            // ProcessedAtUtc khali hai, is liye Stripe ka agla retry dobara koshish karega.
            _logger.LogInformation("Stripe event {EventId} ({Type}) baad me: {Reason}",
                stripeEvent.Id, stripeEvent.Type, ex.Message);
            return (409, ex.Message);
        }
        catch (Exception ex)
        {
            // ProcessedAtUtc khali rehta hai, to Stripe ka retry dobara koshish karega
            // (invoice row par takraav bhi yahin aata hai: retry taaza data par chalega)
            _logger.LogError(ex, "Stripe event {EventId} process nahi ho saka", stripeEvent.Id);
            return (500, "Processing fail hui.");
        }
    }

    // Har handler sirf changes stage karta hai, save HandleWebhookAsync karta hai
    private async Task<string> ProcessEventAsync(Stripe.Event stripeEvent)
    {
        switch (stripeEvent.Type)
        {
            case "checkout.session.completed":
            case "checkout.session.async_payment_succeeded":
            {
                if (stripeEvent.Data.Object is not CheckoutSession session)
                    return IgnoredEvent;

                // Der se hone wali payment me completed ke waqt paise abhi nahi aaye hote
                if (stripeEvent.Type == "checkout.session.completed" && session.PaymentStatus != "paid")
                    return "Intezar: payment abhi paid nahi hui.";

                return await CompleteAttemptAsync(session);
            }

            case "checkout.session.async_payment_failed":
            {
                if (stripeEvent.Data.Object is not CheckoutSession session)
                    return IgnoredEvent;
                return await MarkAttemptAsync(session, PaymentAttemptStatus.Failed, "Payment fail hui.");
            }

            case "checkout.session.expired":
            {
                if (stripeEvent.Data.Object is not CheckoutSession session)
                    return IgnoredEvent;
                return await MarkAttemptAsync(session, PaymentAttemptStatus.Expired, "Session expire ho gaya.");
            }

            case "charge.refunded":
            {
                if (stripeEvent.Data.Object is not Stripe.Charge charge)
                    return IgnoredEvent;
                return await ApplyWebhookRefundAsync(charge, stripeEvent.Created);
            }

            default:
                return IgnoredEvent;
        }
    }

    private async Task<string> CompleteAttemptAsync(CheckoutSession session)
    {
        var attempt = await _db.PaymentAttempts
            .FirstOrDefaultAsync(a => a.ProviderSessionId == session.Id);

        if (attempt == null)
            return UnmatchedSession;

        // Invoice pehle, phir taaza attempt: isi waqt commit hua doosra event yahan nazar aaye ya 409/500 de
        var invoice = await LoadInvoiceAndRefreshAsync(attempt);

        // Naye checkout ki wajah se band (Expired) session par bhi payment ho sakti hai, wo bhi darj hogi
        if (attempt.Status != PaymentAttemptStatus.Pending &&
            attempt.Status != PaymentAttemptStatus.Expired &&
            attempt.Status != PaymentAttemptStatus.Failed)
            return $"Ignored: attempt status {attempt.Status}.";

        // Stripe ne jo amount liya wo hamare record se match kare
        var expectedMinor = InvoiceRules.ToMinorUnits(attempt.Amount);
        var currencyOk = string.Equals(session.Currency, attempt.Currency, StringComparison.OrdinalIgnoreCase);

        if (session.AmountTotal != expectedMinor || !currencyOk)
        {
            var reason = "Amount ya currency hamare record se match nahi hui. " +
                         $"Stripe: {session.AmountTotal} {session.Currency?.ToUpperInvariant()}, " +
                         $"record: {expectedMinor} {attempt.Currency} (minor units).";

            // Faisla aur refund asal pakri gayi raqam par hon (record wali raqam upar reason me mehfooz hai)
            UseCapturedAmount(attempt, session);
            MarkNeedsReview(attempt, invoice, session, reason);
            return "NeedsReview: amount/currency mismatch.";
        }

        // Darj hone se pehle hi kuch paisa wapis ho chuka: poori raqam likhna ghalat hoga, insan dekhe.
        // (Amount match ho chuka, is liye yahan Amount hi pakri gayi raqam hai.)
        if (attempt.RefundedAmount > 0)
        {
            MarkNeedsReview(attempt, invoice, session, "Payment darj hone se pehle refund aa chuka tha.");
            return "NeedsReview: pehle se refund.";
        }

        var paidBefore = invoice == null ? 0m : await _ledger.GetPaidAsync(invoice.Id);

        if (invoice == null ||
            !InvoiceRules.IsCollectable(invoice) ||
            attempt.Amount > invoice.Total - paidBefore)
        {
            MarkNeedsReview(attempt, invoice, session, "Payment ke waqt invoice ki halat badal chuki thi.");
            return "NeedsReview: invoice ki halat badal gayi.";
        }

        var now = DateTime.UtcNow;
        _ledger.StagePayment(
            invoice,
            paidBefore,
            attempt.Amount,
            now,
            "Stripe",
            "Stripe session " + session.Id,
            attempt.Id,
            attempt.CreatedByUserId);

        attempt.Status = PaymentAttemptStatus.Succeeded;
        attempt.ProviderPaymentIntentId = session.PaymentIntentId;
        attempt.FailureReason = null;
        attempt.CompletedAtUtc = now;

        _audit.Stage("PaymentSucceeded", AuditEntity, invoice.Id.ToString(),
            $"Stripe, {attempt.Currency} {attempt.Amount:N2}, attempt {attempt.Id}");

        return "Succeeded: payment record ho gayi.";
    }

    // Paisa Stripe ne le liya magar hum khud darj nahi kar sakte: insan faisla karega
    private void MarkNeedsReview(PaymentAttempt attempt, Invoice? invoice, CheckoutSession session, string reason)
    {
        attempt.Status = PaymentAttemptStatus.NeedsReview;
        attempt.FailureReason = Truncate(reason, ReasonLength);
        attempt.ProviderPaymentIntentId = session.PaymentIntentId ?? attempt.ProviderPaymentIntentId;
        attempt.CompletedAtUtc = DateTime.UtcNow;
        GuardInvoice(invoice);

        _audit.Stage("PaymentNeedsReview", AuditEntity, attempt.InvoiceId.ToString(),
            $"Attempt {attempt.Id}: {reason}");
    }

    // Mismatch par attempt ki Amount/Currency wahi jo Stripe ne asal me li. Is ke baad resolve (apply)
    // aur refund dono sahi raqam par chalte hain: zyada liya gaya paisa poora wapis ho sakta hai.
    private static void UseCapturedAmount(PaymentAttempt attempt, CheckoutSession session)
    {
        if (session.AmountTotal is long captured && captured > 0)
            attempt.Amount = InvoiceRules.FromMinorUnits(captured);

        var currency = session.Currency?.Trim().ToUpperInvariant();
        if (!string.IsNullOrEmpty(currency) && currency.Length == 3)
            attempt.Currency = currency;
    }

    private async Task<string> MarkAttemptAsync(CheckoutSession session, string status, string reason)
    {
        var attempt = await _db.PaymentAttempts
            .FirstOrDefaultAsync(a => a.ProviderSessionId == session.Id);

        if (attempt == null)
            return UnmatchedSession;

        // Sirf khula attempt fail/expire hota hai. Kamyab ya faisla shuda kabhi wapis nahi badalta.
        if (attempt.Status != PaymentAttemptStatus.Pending)
            return $"Ignored: attempt status {attempt.Status}.";

        attempt.Status = status;
        attempt.FailureReason = reason;
        attempt.CompletedAtUtc = DateTime.UtcNow;

        _audit.Stage("PaymentFailed", AuditEntity, attempt.InvoiceId.ToString(),
            $"{reason} Attempt {attempt.Id} ({status}).");

        return $"{status}: {reason}";
    }

    // Stripe dashboard ya hamari API se hua refund: sirf naya (delta) hissa darj hota hai
    private async Task<string> ApplyWebhookRefundAsync(Stripe.Charge charge, DateTime eventCreatedUtc)
    {
        var paymentIntentId = charge.PaymentIntentId;
        if (string.IsNullOrWhiteSpace(paymentIntentId))
            return "Unmatched: charge ke saath payment intent nahi.";

        var attempt = await _db.PaymentAttempts
            .Where(a => a.ProviderPaymentIntentId == paymentIntentId)
            .OrderByDescending(a => a.Id)
            .FirstOrDefaultAsync();

        if (attempt == null)
        {
            // Payment intent attempt par checkout.session.completed ke waqt likha jata hai. Refund us se pehle
            // aa jaye to abhi "Unmatched" likhna refund ko hamesha ke liye kho deta (baad me poori payment darj hoti).
            if (await MayAwaitCompletionAsync(charge, eventCreatedUtc))
                throw new WebhookRetryException(RetryRefundMessage);

            return $"Unmatched: hamare record me ye payment intent ({paymentIntentId}) nahi.";
        }

        // Invoice pehle, phir taaza attempt: API refund isi waqt commit hua ho to uski RefundedAmount yahan dikhe,
        // aur baad me commit ho to invoice row par takraav (500, Stripe dobara bhejega)
        var invoice = await LoadInvoiceAndRefreshAsync(attempt);

        // amount_refunded kul jama hai (har refund ke baad barhta hai)
        var refundedTotal = InvoiceRules.FromMinorUnits(charge.AmountRefunded);
        var delta = refundedTotal - attempt.RefundedAmount;
        if (delta <= 0)
            return "Ignored: refund pehle se darj.";

        GuardInvoice(invoice);

        // Ledger me sirf utna wapis jitna is attempt ka darj hai (NeedsReview ki payment kabhi darj nahi hui)
        if (invoice != null &&
            (attempt.Status == PaymentAttemptStatus.Succeeded || attempt.Status == PaymentAttemptStatus.Refunded))
        {
            var booked = await BookedForAttemptAsync(attempt.Id);
            var reversal = Math.Min(delta, Math.Max(0m, booked));

            if (reversal > 0)
            {
                var paidBefore = await _ledger.GetPaidAsync(invoice.Id);
                _ledger.StagePayment(
                    invoice,
                    paidBefore,
                    -reversal,
                    DateTime.UtcNow,
                    "Stripe refund",
                    "Stripe refund " + paymentIntentId,
                    attempt.Id,
                    null);
            }
        }

        attempt.RefundedAmount = refundedTotal;
        if (refundedTotal >= attempt.Amount)
            attempt.Status = PaymentAttemptStatus.Refunded;

        _audit.Stage("PaymentRefunded", AuditEntity, attempt.InvoiceId.ToString(),
            $"Stripe webhook, {attempt.Currency} {delta:N2} wapis, attempt {attempt.Id}");

        return $"Refunded: {attempt.Currency} {delta:N2} wapis darj.";
    }

    // Charge ke metadata me hamara invoiceId ho (StripeGateway payment_intent_data me deta hai, Stripe usay charge par
    // bhi likhta hai; na ho to seedha Unmatched) aur us invoice ka koi attempt abhi payment intent ke baghair khula ho:
    // shayad iski payment ka event abhi aana hai.
    private async Task<bool> MayAwaitCompletionAsync(Stripe.Charge charge, DateTime eventCreatedUtc)
    {
        if (DateTime.UtcNow - eventCreatedUtc > RefundRetryWindow)
            return false;

        var raw = charge.Metadata != null && charge.Metadata.TryGetValue("invoiceId", out var value) ? value : null;
        if (!int.TryParse(raw, out var invoiceId))
            return false;

        return await _db.PaymentAttempts.AnyAsync(a =>
            a.InvoiceId == invoiceId &&
            a.ProviderPaymentIntentId == null &&
            (a.Status == PaymentAttemptStatus.Pending ||
             a.Status == PaymentAttemptStatus.Expired ||
             a.Status == PaymentAttemptStatus.Failed));
    }

    // ---------- 3. Attempts ki list ----------
    public async Task<(IReadOnlyList<StripeAttemptSummary>? Items, string? Error)> ListAttemptsAsync(
        string? status, int? invoiceId)
    {
        var query = _db.PaymentAttempts.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(status))
        {
            var trimmed = status.Trim();
            var known = AllStatuses.FirstOrDefault(s => string.Equals(s, trimmed, StringComparison.OrdinalIgnoreCase));
            if (known == null)
                return (null, $"Status in me se hona chahiye: {string.Join(", ", AllStatuses)}.");

            query = query.Where(a => a.Status == known);
        }

        if (invoiceId.HasValue)
        {
            var id = invoiceId.Value;
            query = query.Where(a => a.InvoiceId == id);
        }

        var attempts = await query
            .OrderByDescending(a => a.CreatedAtUtc)
            .ThenByDescending(a => a.Id)
            .Take(MaxListSize)
            .ToListAsync();

        var invoiceIds = attempts.Select(a => a.InvoiceId).Distinct().ToList();
        var numbers = await _db.Invoices
            .AsNoTracking()
            .Where(i => invoiceIds.Contains(i.Id))
            .Select(i => new { i.Id, i.InvoiceNumber })
            .ToDictionaryAsync(x => x.Id, x => x.InvoiceNumber);

        var items = attempts
            .Select(a => StripeAttemptSummary.From(a, numbers.TryGetValue(a.InvoiceId, out var number) ? number : null))
            .ToList();

        return (items, null);
    }

    // ---------- 4. NeedsReview ka faisla ----------
    public async Task<StripeAttemptResult> ResolveAttemptAsync(int attemptId, string? action, string? note, string? userId)
    {
        var normalized = action?.Trim().ToLowerInvariant();
        if (normalized != ResolveAttemptRequest.Apply && normalized != ResolveAttemptRequest.Dismiss)
            return StripeAttemptResult.Invalid("Action 'apply' ya 'dismiss' honi chahiye.");

        var attempt = await _db.PaymentAttempts.FirstOrDefaultAsync(a => a.Id == attemptId);
        if (attempt == null)
            return StripeAttemptResult.Missing("Payment attempt nahi mila.");

        // Invoice pehle, phir taaza attempt: isi waqt aaya refund webhook yahan nazar aaye ya save par 409 de
        var invoice = await LoadInvoiceAndRefreshAsync(attempt);

        if (attempt.Status != PaymentAttemptStatus.NeedsReview)
            return StripeAttemptResult.Invalid(
                $"Sirf NeedsReview attempt ka faisla ho sakta hai. Abhi status: {attempt.Status}.");

        var cleanNote = CleanNote(note);
        var now = DateTime.UtcNow;
        string details;

        if (normalized == ResolveAttemptRequest.Apply)
        {
            if (invoice == null)
                return StripeAttemptResult.Invalid("Is attempt ka invoice maujood nahi.");

            if (!InvoiceRules.IsCollectable(invoice))
                return StripeAttemptResult.Invalid(
                    $"Invoice par ab payment nahi ho sakti. Status: {invoice.Status}.");

            if (!string.Equals(attempt.Currency?.Trim(), invoice.Currency?.Trim(), StringComparison.OrdinalIgnoreCase))
                return StripeAttemptResult.Invalid(
                    $"Attempt ki currency ({attempt.Currency}) invoice ({invoice.Currency}) se alag hai.");

            // Darj sirf wo hota hai jo Stripe ke paas abhi hai: pakri gayi raqam minus pehle ke refund.
            // (Mismatch par webhook Amount ko asal pakri gayi raqam kar deta hai; baqi NeedsReview me dono barabar hain.)
            // Misal: 1200 liye, zaid 200 wapis = 1000 darj. Pehle se 400 wapis = 600 darj, poori raqam nahi.
            var amount = attempt.Amount - attempt.RefundedAmount;
            if (amount <= 0)
                return StripeAttemptResult.Invalid(
                    "Is payment ka sara paisa wapis ho chuka, darj karne ko kuch nahi. Dismiss karein.");

            var paidBefore = await _ledger.GetPaidAsync(invoice.Id);
            var balance = invoice.Total - paidBefore;
            if (amount > balance)
                return StripeAttemptResult.Invalid($"Payment baqaya se zyada hai. Baqaya: {balance}");

            _ledger.StagePayment(
                invoice,
                paidBefore,
                amount,
                now,
                "Stripe",
                Truncate("Resolved: " + (cleanNote ?? "Stripe session " + attempt.ProviderSessionId), PaymentNoteLength),
                attempt.Id,
                userId);

            attempt.Status = PaymentAttemptStatus.Succeeded;
            attempt.CompletedAtUtc ??= now;
            details = $"apply: {attempt.Currency} {amount:N2} payment darj, invoice {invoice.Id}";
            if (attempt.RefundedAmount > 0)
                details += $", Stripe pehle {attempt.RefundedAmount:N2} wapis kar chuka";
        }
        else
        {
            GuardInvoice(invoice);
            attempt.Status = PaymentAttemptStatus.Resolved;
            details = $"dismiss: koi payment darj nahi, invoice {attempt.InvoiceId}";
        }

        attempt.ResolvedByUserId = userId;
        attempt.ResolvedAtUtc = now;
        attempt.ResolutionNote = cleanNote;

        _audit.Stage("PaymentAttemptResolved", "PaymentAttempt", attempt.Id.ToString(),
            cleanNote == null ? details : $"{details}. {cleanNote}");
        await _db.SaveChangesAsync();

        return StripeAttemptResult.Success(
            StripeAttemptSummary.From(attempt, await InvoiceNumberAsync(attempt.InvoiceId)));
    }

    // ---------- 5. Stripe se paisa wapis (admin) ----------
    public async Task<StripeAttemptResult> RefundAttemptAsync(int attemptId, string? note, string? userId)
    {
        // Setting na ho to Stripe ya database ko chhone se pehle hi saaf 500
        EnsureSecretKey();

        var attempt = await _db.PaymentAttempts.FirstOrDefaultAsync(a => a.Id == attemptId);
        if (attempt == null)
            return StripeAttemptResult.Missing("Payment attempt nahi mila.");

        // Invoice (tracked) aur paidBefore Stripe call se PEHLE. Stripe isi refund ka charge.refunded webhook
        // hamare jawab se pehle bhi bhej sakta hai: wo darj ho chuka ho to invoice ka rowversion badal chuka hoga
        // aur neeche wala save takrayega, yani refund ledger me sirf ek dafa darj hoga.
        var invoice = await LoadInvoiceAndRefreshAsync(attempt);

        if (attempt.Status != PaymentAttemptStatus.Succeeded && attempt.Status != PaymentAttemptStatus.NeedsReview)
            return StripeAttemptResult.Invalid(
                $"Sirf Succeeded ya NeedsReview payment wapis ho sakti hai. Abhi status: {attempt.Status}.");

        var paymentIntentId = attempt.ProviderPaymentIntentId;
        if (string.IsNullOrWhiteSpace(paymentIntentId))
            return StripeAttemptResult.Invalid("Is attempt ka Stripe payment intent maujood nahi.");

        var remaining = attempt.Amount - attempt.RefundedAmount;
        if (remaining <= 0)
            return StripeAttemptResult.Invalid("Is payment me wapis karne ko kuch baqi nahi.");

        var paidBefore = invoice == null ? 0m : await _ledger.GetPaidAsync(invoice.Id);
        var booked = await BookedForAttemptAsync(attempt.Id);

        // Key me abhi tak ka refunded amount: do tez clicks Stripe par ek hi refund banate hain,
        // magar baad me baqi raqam ka naya refund alag key se jata hai
        var idempotencyKey = $"refund-attempt-{attempt.Id}-{InvoiceRules.ToMinorUnits(attempt.RefundedAmount)}";
        var refund = await _gateway.RefundPaymentIntentAsync(
            paymentIntentId, InvoiceRules.ToMinorUnits(remaining), idempotencyKey);

        if (string.Equals(refund.Status, "failed", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(refund.Status, "canceled", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Stripe refund {RefundId} ka status {Status} (attempt {AttemptId})",
                refund.Id, refund.Status, attempt.Id);
            return StripeAttemptResult.ProviderFailed($"Stripe ne refund qabool nahi kiya (status {refund.Status}).");
        }

        var cleanNote = CleanNote(note);
        GuardInvoice(invoice);

        // NeedsReview ki payment ledger me kabhi darj nahi hui, is liye uski reversal bhi nahi.
        // Succeeded me bhi sirf utna ulat-ta hai jitna is attempt ka ledger me darj hai.
        var reversal = Math.Min(remaining, Math.Max(0m, booked));
        if (attempt.Status == PaymentAttemptStatus.Succeeded && invoice != null && reversal > 0)
        {
            var paymentNote = "Stripe refund " + paymentIntentId + (cleanNote == null ? "" : " - " + cleanNote);
            _ledger.StagePayment(
                invoice,
                paidBefore,
                -reversal,
                DateTime.UtcNow,
                "Stripe refund",
                Truncate(paymentNote, PaymentNoteLength),
                attempt.Id,
                userId);
        }

        attempt.RefundedAmount += remaining;
        attempt.Status = PaymentAttemptStatus.Refunded;
        attempt.ProviderRefundId = refund.Id;

        var details = $"Stripe refund {refund.Id}, {attempt.Currency} {remaining:N2}, attempt {attempt.Id}";
        _audit.Stage("PaymentRefunded", AuditEntity, attempt.InvoiceId.ToString(),
            cleanNote == null ? details : $"{details}. {cleanNote}");

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // Paisa Stripe se ja chuka. Isi waqt invoice badla, aksar isi refund ka webhook hum se pehle darj kar deta hai.
            _logger.LogWarning(ex, "Stripe refund {RefundId} ho gaya magar attempt {AttemptId} save karte waqt takraav",
                refund.Id, attempt.Id);

            var expectedRefunded = attempt.RefundedAmount;
            _db.ChangeTracker.Clear();

            var fresh = await _db.PaymentAttempts.FirstOrDefaultAsync(a => a.Id == attemptId);
            if (fresh != null && fresh.RefundedAmount >= expectedRefunded)
            {
                // Webhook ne yahi refund darj kar diya. Stripe refund id aur admin ka note phir bhi record me rahen.
                await LinkApiRefundAsync(fresh, refund.Id, cleanNote);
                return StripeAttemptResult.Success(
                    StripeAttemptSummary.From(fresh, await InvoiceNumberAsync(fresh.InvoiceId)));
            }

            // Webhook ya uska retry refund darj kar dega, is liye admin dobara refund na kare
            return StripeAttemptResult.Conflict(
                "Stripe par refund ho gaya, magar isi waqt record badal gaya. Stripe webhook isay darj kar dega, dobara refund na karein.");
        }

        return StripeAttemptResult.Success(
            StripeAttemptSummary.From(attempt, await InvoiceNumberAsync(attempt.InvoiceId)));
    }

    // API se kiya refund jise webhook pehle darj kar chuka: sirf refund id aur note jorna (paisa dobara nahi)
    private async Task LinkApiRefundAsync(PaymentAttempt attempt, string refundId, string? note)
    {
        if (string.IsNullOrEmpty(attempt.ProviderRefundId))
            attempt.ProviderRefundId = refundId;

        var details = $"Stripe refund {refundId} (admin), webhook ne pehle darj kiya, attempt {attempt.Id}";
        _audit.Stage("PaymentRefundLinked", AuditEntity, attempt.InvoiceId.ToString(),
            note == null ? details : $"{details}. {note}");

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex)
        {
            // Refund ledger me pehle se darj hai; sirf id/note reh gaye, admin ko kamyabi hi dikhti hai
            _logger.LogWarning(ex, "Stripe refund {RefundId} ki id/note attempt {AttemptId} par save nahi hui",
                refundId, attempt.Id);
        }
    }

    // Pehle invoice (tracked), phir attempt dobara parhna. Is se pehle commit hua koi bhi paise ka kaam
    // taaza attempt me nazar aata hai, aur jo baad me commit ho wo invoice ke rowversion par takrata hai.
    private async Task<Invoice?> LoadInvoiceAndRefreshAsync(PaymentAttempt attempt)
    {
        var invoice = await _db.Invoices.FirstOrDefaultAsync(i => i.Id == attempt.InvoiceId);
        await _db.Entry(attempt).ReloadAsync();
        return invoice;
    }

    // Attempt ki halat badalne wala har kaam invoice row bhi "modified" karta hai (value wahi rehti hai).
    // SQL Server rowversion badalta hai, is liye isi invoice par saath chalne wala doosra kaam 409 paata hai.
    private void GuardInvoice(Invoice? invoice)
    {
        if (invoice != null)
            _db.Entry(invoice).Property(i => i.Status).IsModified = true;
    }

    // Is attempt ki ledger me darj kul raqam (payment minus pehle ki reversals)
    private async Task<decimal> BookedForAttemptAsync(int attemptId) =>
        await _db.Payments
            .Where(p => p.PaymentAttemptId == attemptId)
            .SumAsync(p => (decimal?)p.Amount) ?? 0m;

    private async Task<string?> InvoiceNumberAsync(int invoiceId) =>
        await _db.Invoices
            .Where(i => i.Id == invoiceId)
            .Select(i => i.InvoiceNumber)
            .FirstOrDefaultAsync();

    private string ConfigOrDefault(string key, string fallback)
    {
        var value = _config[key];
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private void EnsureSecretKey()
    {
        if (string.IsNullOrWhiteSpace(_config["Stripe:SecretKey"]))
            throw new StripeConfigurationException("Stripe:SecretKey set nahi hai.");
    }

    private static string? CleanNote(string? note) =>
        string.IsNullOrWhiteSpace(note) ? null : Truncate(note.Trim(), ReasonLength);

    private static string? Truncate(string? value, int max) =>
        value == null || value.Length <= max ? value : value[..max];

    // Event abhi process nahi ho sakta magar baad me ho jayega: webhook non-2xx deta hai taake Stripe dobara bheje
    private sealed class WebhookRetryException : Exception
    {
        public WebhookRetryException(string message) : base(message)
        {
        }
    }
}
