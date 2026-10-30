using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using LedgerMind.Models;
using LedgerMind.Services;
using LedgerMind.Tests.Infrastructure;

namespace LedgerMind.Tests.StripePayments;

// Stripe events ki tarteeb pakki nahi, aur Stripe kabhi record se alag raqam le leta hai.
// Yahan: refund apni payment se pehle aaye to retry, aur review/refund asal pakri gayi raqam par.
public class StripeEventOrderTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public StripeEventOrderTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // charge.refunded jaisa asli Stripe bhejta hai: checkout ka metadata (invoiceId) charge par bhi hota hai
    private static string RefundedWithInvoiceMetadata(
        string eventId,
        string paymentIntentId,
        long amountMinor,
        long refundedMinor,
        int invoiceId,
        DateTimeOffset? created = null)
    {
        var charge = new JsonObject
        {
            ["id"] = $"ch_test_{Guid.NewGuid():N}",
            ["object"] = "charge",
            ["amount"] = amountMinor,
            ["amount_refunded"] = refundedMinor,
            ["currency"] = "pkr",
            ["payment_intent"] = paymentIntentId,
            ["refunded"] = refundedMinor >= amountMinor,
            ["paid"] = true,
            ["status"] = "succeeded",
            ["metadata"] = new JsonObject
            {
                ["invoiceId"] = invoiceId.ToString(),
                ["invoiceNumber"] = "INV-order-test"
            }
        };

        var stripeEvent = new JsonObject
        {
            ["id"] = eventId,
            ["object"] = "event",
            ["api_version"] = "2024-06-20",
            ["created"] = (created ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds(),
            ["livemode"] = false,
            ["pending_webhooks"] = 1,
            ["request"] = new JsonObject { ["id"] = null, ["idempotency_key"] = null },
            ["type"] = "charge.refunded",
            ["data"] = new JsonObject { ["object"] = charge }
        };

        return stripeEvent.ToJsonString();
    }

    private static string Completed(PaymentAttempt attempt, long amountMinor, string paymentIntentId, string currency = "PKR") =>
        StripeWebhookBuilder.CheckoutSessionEvent(
            "checkout.session.completed", attempt.ProviderSessionId, amountMinor, currency,
            paymentIntentId: paymentIntentId);

    // ---------- Refund payment se pehle ----------

    [Fact]
    public async Task Refund_arriving_before_its_payment_is_retried_and_reversed_after_the_payment()
    {
        var invoice = await StripeTestHelpers.NewInvoiceAsync(_factory, subtotal: 1000m);
        var attempt = await TestData.AddPaymentAttemptAsync(_factory, invoice.Id, 1000m);
        var paymentIntentId = StripeTestHelpers.NewPaymentIntentId();
        var refundEventId = StripeWebhookBuilder.NewEventId();
        var refundJson = RefundedWithInvoiceMetadata(refundEventId, paymentIntentId, 100000, 40000, invoice.Id);

        // 1. Refund pehle: abhi darj nahi, non-2xx taake Stripe dobara bheje
        var early = await StripeTestHelpers.SendWebhookAsync(_factory, refundJson);

        await early.AssertStatusAsync(HttpStatusCode.Conflict);
        Assert.StartsWith("Retry", await early.ReadMessageAsync());
        var pending = await StripeTestHelpers.GetWebhookEventAsync(_factory, refundEventId);
        Assert.NotNull(pending);
        Assert.Null(pending!.ProcessedAtUtc);
        Assert.Empty(await StripeTestHelpers.GetPaymentsAsync(_factory, invoice.Id));
        var untouched = await StripeTestHelpers.GetAttemptAsync(_factory, attempt.Id);
        Assert.Equal(PaymentAttemptStatus.Pending, untouched.Status);
        Assert.Equal(0m, untouched.RefundedAmount);

        // 2. Payment ka event: poori 1000 darj
        var completed = await StripeTestHelpers.SendWebhookAsync(_factory, Completed(attempt, 100000, paymentIntentId));
        await completed.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Equal(1000m, Assert.Single(await StripeTestHelpers.GetPaymentsAsync(_factory, invoice.Id)).Amount);

        // 3. Stripe ka retry (wahi event id): ab refund ulat jata hai
        var retried = await StripeTestHelpers.SendWebhookAsync(_factory, refundJson);

        await retried.AssertStatusAsync(HttpStatusCode.OK);
        Assert.StartsWith("Refunded", await retried.ReadMessageAsync());
        var amounts = (await StripeTestHelpers.GetPaymentsAsync(_factory, invoice.Id)).Select(p => p.Amount).ToList();
        Assert.Equal(new[] { 1000m, -400m }, amounts);
        Assert.Equal(InvoiceStatus.Partial, (await StripeTestHelpers.GetInvoiceAsync(_factory, invoice.Id)).Status);

        var saved = await StripeTestHelpers.GetAttemptAsync(_factory, attempt.Id);
        Assert.Equal(PaymentAttemptStatus.Succeeded, saved.Status);
        Assert.Equal(400m, saved.RefundedAmount);
        var processed = await StripeTestHelpers.GetWebhookEventAsync(_factory, refundEventId);
        Assert.NotNull(processed!.ProcessedAtUtc);
    }

    [Fact]
    public async Task Refund_for_known_invoice_without_open_checkout_is_unmatched()
    {
        var invoice = await StripeTestHelpers.NewInvoiceAsync(_factory, subtotal: 1000m);
        var eventId = StripeWebhookBuilder.NewEventId();

        var response = await StripeTestHelpers.SendWebhookAsync(_factory,
            RefundedWithInvoiceMetadata(eventId, StripeTestHelpers.NewPaymentIntentId(), 100000, 100000, invoice.Id));

        await response.AssertStatusAsync(HttpStatusCode.OK);
        Assert.StartsWith("Unmatched", await response.ReadMessageAsync());
        Assert.NotNull((await StripeTestHelpers.GetWebhookEventAsync(_factory, eventId))!.ProcessedAtUtc);
    }

    [Fact]
    public async Task Very_old_unmatched_refund_is_not_retried_forever()
    {
        var invoice = await StripeTestHelpers.NewInvoiceAsync(_factory, subtotal: 1000m);
        await TestData.AddPaymentAttemptAsync(_factory, invoice.Id, 1000m);

        var response = await StripeTestHelpers.SendWebhookAsync(_factory,
            RefundedWithInvoiceMetadata(StripeWebhookBuilder.NewEventId(), StripeTestHelpers.NewPaymentIntentId(),
                100000, 100000, invoice.Id, created: DateTimeOffset.UtcNow.AddDays(-4)));

        await response.AssertStatusAsync(HttpStatusCode.OK);
        Assert.StartsWith("Unmatched", await response.ReadMessageAsync());
    }

    // ---------- Asal pakri gayi raqam ----------

    [Fact]
    public async Task Overcharge_keeps_the_captured_amount_and_apply_needs_the_extra_refunded_first()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var (invoice, attempt, _) = await StripeTestHelpers.CreateNeedsReviewAttemptAsync(
            _factory, invoiceTotal: 1000m, chargedMinor: 120000);

        // Review me wahi raqam jo Stripe ne li; record wali raqam reason me
        Assert.Equal(1200m, attempt.Amount);
        Assert.Equal("PKR", attempt.Currency);
        Assert.Contains("100000", attempt.FailureReason);

        var response = await client.PostAsJsonAsync(StripeTestHelpers.ResolveUrl(attempt.Id), new { action = "apply" });

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Contains("baqaya", await response.ReadMessageAsync());
        Assert.Empty(await StripeTestHelpers.GetPaymentsAsync(_factory, invoice.Id));
        Assert.Equal(PaymentAttemptStatus.NeedsReview, (await StripeTestHelpers.GetAttemptAsync(_factory, attempt.Id)).Status);
    }

    [Fact]
    public async Task Currency_mismatch_keeps_the_captured_currency_so_apply_is_refused()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var invoice = await StripeTestHelpers.NewInvoiceAsync(_factory, subtotal: 1000m, currency: "PKR");
        var attempt = await TestData.AddPaymentAttemptAsync(_factory, invoice.Id, 1000m);

        var webhook = await StripeTestHelpers.SendWebhookAsync(_factory,
            Completed(attempt, 100000, StripeTestHelpers.NewPaymentIntentId(), currency: "USD"));
        await webhook.AssertStatusAsync(HttpStatusCode.OK);

        var review = await StripeTestHelpers.GetAttemptAsync(_factory, attempt.Id);
        Assert.Equal(PaymentAttemptStatus.NeedsReview, review.Status);
        Assert.Equal("USD", review.Currency);

        var response = await client.PostAsJsonAsync(StripeTestHelpers.ResolveUrl(attempt.Id), new { action = "apply" });

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Empty(await StripeTestHelpers.GetPaymentsAsync(_factory, invoice.Id));
        Assert.Equal(InvoiceStatus.Approved, (await StripeTestHelpers.GetInvoiceAsync(_factory, invoice.Id)).Status);
    }

    [Fact]
    public async Task Apply_after_refund_that_came_before_booking_records_only_what_stripe_still_holds()
    {
        var (_, manager) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var admin = await TestAuth.AdminClientAsync(_factory);
        var invoice = await StripeTestHelpers.NewInvoiceAsync(_factory, subtotal: 1000m);
        var paymentIntentId = StripeTestHelpers.NewPaymentIntentId();
        var attempt = await TestData.AddPaymentAttemptAsync(_factory, invoice.Id, 1000m, paymentIntentId: paymentIntentId);

        // 400 ka refund darj hone se pehle, phir payment: NeedsReview
        var refund = await StripeTestHelpers.SendWebhookAsync(
            _factory, StripeWebhookBuilder.ChargeRefundedEvent(paymentIntentId, 100000, 40000, "PKR"));
        await refund.AssertStatusAsync(HttpStatusCode.OK);
        var completed = await StripeTestHelpers.SendWebhookAsync(_factory, Completed(attempt, 100000, paymentIntentId));
        await completed.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Equal("NeedsReview: pehle se refund.", await completed.ReadMessageAsync());

        // Apply: sirf 1000 - 400 = 600 darj
        var apply = await manager.PostAsJsonAsync(StripeTestHelpers.ResolveUrl(attempt.Id), new { action = "apply" });

        await apply.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Equal(600m, Assert.Single(await StripeTestHelpers.GetPaymentsAsync(_factory, invoice.Id)).Amount);
        Assert.Equal(InvoiceStatus.Partial, (await StripeTestHelpers.GetInvoiceAsync(_factory, invoice.Id)).Status);
        Assert.Equal(PaymentAttemptStatus.Succeeded, (await StripeTestHelpers.GetAttemptAsync(_factory, attempt.Id)).Status);

        // Baqi refund: Stripe se 600, ledger me bhi 600 ulta, hisab barabar
        var rest = await admin.PostAsJsonAsync(StripeTestHelpers.RefundUrl(attempt.Id), new { note = "baqi wapis" });

        await rest.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Contains(_factory.FakeStripe.Refunds,
            r => r.PaymentIntentId == paymentIntentId && r.AmountMinor == 60000);
        Assert.Equal(0m, (await StripeTestHelpers.GetPaymentsAsync(_factory, invoice.Id)).Sum(p => p.Amount));
        Assert.Equal(InvoiceStatus.Approved, (await StripeTestHelpers.GetInvoiceAsync(_factory, invoice.Id)).Status);

        var saved = await StripeTestHelpers.GetAttemptAsync(_factory, attempt.Id);
        Assert.Equal(PaymentAttemptStatus.Refunded, saved.Status);
        Assert.Equal(1000m, saved.RefundedAmount);
    }

    [Fact]
    public async Task Checkout_still_returns_link_and_audits_with_the_invoice_guard()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var invoice = await StripeTestHelpers.NewInvoiceAsync(_factory, subtotal: 750m);

        var response = await client.PostAsync(StripeTestHelpers.CheckoutUrl(invoice.Id), null);

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var json = await response.ReadJsonAsync();
        Assert.Equal(750m, json.GetProperty("amount").GetDecimal());
        var attempt = Assert.Single(await StripeTestHelpers.GetAttemptsForInvoiceAsync(_factory, invoice.Id));
        Assert.Equal(PaymentAttemptStatus.Pending, attempt.Status);
        Assert.Equal(InvoiceRules.ToMinorUnits(750m), Assert.Single(_factory.FakeStripe.CreatedSessions,
            r => r.ClientReferenceId == invoice.Id.ToString()).AmountMinor);
        Assert.Equal(InvoiceStatus.Approved, (await StripeTestHelpers.GetInvoiceAsync(_factory, invoice.Id)).Status);
        Assert.Single(await StripeTestHelpers.GetAuditAsync(_factory, "PaymentInitiated", invoice.Id.ToString()));
    }
}
