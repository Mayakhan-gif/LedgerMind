using System.Net;
using System.Text;
using LedgerMind.Models;
using LedgerMind.Tests.Infrastructure;

namespace LedgerMind.Tests.StripePayments;

public class StripeWebhookTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public StripeWebhookTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static string Completed(
        PaymentAttempt attempt,
        long amountMinor,
        string paymentIntentId,
        string currency = "PKR",
        string paymentStatus = "paid",
        string? eventId = null) =>
        StripeWebhookBuilder.CheckoutSessionEvent(
            "checkout.session.completed",
            attempt.ProviderSessionId,
            amountMinor,
            currency,
            paymentStatus,
            paymentIntentId,
            eventId);

    // ---------- Signature ----------

    [Fact]
    public async Task Webhook_without_signature_returns_400()
    {
        var json = StripeWebhookBuilder.CheckoutSessionEvent("checkout.session.completed", "cs_test_nosig", 100000, "PKR");
        var request = new HttpRequestMessage(HttpMethod.Post, StripeTestHelpers.WebhookUrl)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        var response = await _factory.CreateClient().SendAsync(request);

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Equal("Signature maujood nahi.", await response.ReadMessageAsync());
    }

    [Fact]
    public async Task Webhook_with_wrong_secret_returns_400_and_records_nothing()
    {
        var eventId = StripeWebhookBuilder.NewEventId();
        var json = StripeWebhookBuilder.CheckoutSessionEvent(
            "checkout.session.completed", "cs_test_badsig", 100000, "PKR", eventId: eventId);

        var response = await _factory.CreateClient().SendAsync(
            StripeWebhookBuilder.SignedRequest(json, secret: "whsec_someone_else"));

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Equal("Signature ghalat hai.", await response.ReadMessageAsync());
        Assert.Null(await StripeTestHelpers.GetWebhookEventAsync(_factory, eventId));
    }

    [Fact]
    public async Task Webhook_with_signature_of_other_payload_returns_400()
    {
        var invoice = await StripeTestHelpers.NewInvoiceAsync(_factory);
        var attempt = await TestData.AddPaymentAttemptAsync(_factory, invoice.Id, 1000m);
        var json = Completed(attempt, 100000, StripeTestHelpers.NewPaymentIntentId());
        var otherSignature = StripeWebhookBuilder.Sign(json + " ", TestWebApplicationFactory.StripeWebhookSecret);

        var response = await _factory.CreateClient().SendAsync(
            StripeWebhookBuilder.SignedRequest(json, signatureOverride: otherSignature));

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Empty(await StripeTestHelpers.GetPaymentsAsync(_factory, invoice.Id));
        Assert.Equal(PaymentAttemptStatus.Pending, (await StripeTestHelpers.GetAttemptAsync(_factory, attempt.Id)).Status);
    }

    // ---------- checkout.session.completed ----------

    [Fact]
    public async Task Completed_paid_session_records_payment_and_marks_invoice_paid()
    {
        var invoice = await StripeTestHelpers.NewInvoiceAsync(_factory, subtotal: 1000m);
        var attempt = await TestData.AddPaymentAttemptAsync(_factory, invoice.Id, 1000m);
        var paymentIntentId = StripeTestHelpers.NewPaymentIntentId();

        var response = await StripeTestHelpers.SendWebhookAsync(_factory, Completed(attempt, 100000, paymentIntentId));

        await response.AssertStatusAsync(HttpStatusCode.OK);
        Assert.StartsWith("Succeeded", await response.ReadMessageAsync());

        var payment = Assert.Single(await StripeTestHelpers.GetPaymentsAsync(_factory, invoice.Id));
        Assert.Equal(1000m, payment.Amount);
        Assert.Equal(attempt.Id, payment.PaymentAttemptId);
        Assert.Equal("Stripe", payment.Method);
        Assert.Equal("Stripe session " + attempt.ProviderSessionId, payment.Note);

        Assert.Equal(InvoiceStatus.Paid, (await StripeTestHelpers.GetInvoiceAsync(_factory, invoice.Id)).Status);

        var saved = await StripeTestHelpers.GetAttemptAsync(_factory, attempt.Id);
        Assert.Equal(PaymentAttemptStatus.Succeeded, saved.Status);
        Assert.Equal(paymentIntentId, saved.ProviderPaymentIntentId);
        Assert.NotNull(saved.CompletedAtUtc);
        Assert.Null(saved.FailureReason);

        var audit = Assert.Single(await StripeTestHelpers.GetAuditAsync(_factory, "PaymentSucceeded", invoice.Id.ToString()));
        Assert.Null(audit.UserId);
    }

    [Fact]
    public async Task Payment_keeps_the_user_who_started_checkout()
    {
        var (manager, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var invoice = await StripeTestHelpers.NewInvoiceAsync(_factory, subtotal: 1000m);
        var checkout = await client.PostAsync(StripeTestHelpers.CheckoutUrl(invoice.Id), null);
        await checkout.AssertStatusAsync(HttpStatusCode.OK);
        var attempt = Assert.Single(await StripeTestHelpers.GetAttemptsForInvoiceAsync(_factory, invoice.Id));

        var response = await StripeTestHelpers.SendWebhookAsync(
            _factory, Completed(attempt, 100000, StripeTestHelpers.NewPaymentIntentId()));

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var payment = Assert.Single(await StripeTestHelpers.GetPaymentsAsync(_factory, invoice.Id));
        Assert.Equal(manager.Id, payment.CreatedByUserId);
        Assert.Equal(attempt.Id, payment.PaymentAttemptId);
    }

    [Fact]
    public async Task Completed_payment_for_part_of_total_marks_invoice_partial()
    {
        var invoice = await StripeTestHelpers.NewInvoiceAsync(_factory, subtotal: 1000m);
        var attempt = await TestData.AddPaymentAttemptAsync(_factory, invoice.Id, 400m);

        var response = await StripeTestHelpers.SendWebhookAsync(
            _factory, Completed(attempt, 40000, StripeTestHelpers.NewPaymentIntentId()));

        await response.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Equal(400m, Assert.Single(await StripeTestHelpers.GetPaymentsAsync(_factory, invoice.Id)).Amount);
        Assert.Equal(InvoiceStatus.Partial, (await StripeTestHelpers.GetInvoiceAsync(_factory, invoice.Id)).Status);
    }

    [Fact]
    public async Task Webhook_event_is_recorded_as_processed_with_outcome()
    {
        var invoice = await StripeTestHelpers.NewInvoiceAsync(_factory);
        var attempt = await TestData.AddPaymentAttemptAsync(_factory, invoice.Id, 1000m);
        var eventId = StripeWebhookBuilder.NewEventId();

        var response = await StripeTestHelpers.SendWebhookAsync(
            _factory, Completed(attempt, 100000, StripeTestHelpers.NewPaymentIntentId(), eventId: eventId));

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var record = await StripeTestHelpers.GetWebhookEventAsync(_factory, eventId);
        Assert.NotNull(record);
        Assert.Equal("Stripe", record!.Provider);
        Assert.Equal("checkout.session.completed", record.EventType);
        Assert.NotNull(record.ProcessedAtUtc);
        Assert.StartsWith("Succeeded", record.Outcome);
    }

    [Fact]
    public async Task Same_event_twice_is_processed_only_once()
    {
        var invoice = await StripeTestHelpers.NewInvoiceAsync(_factory);
        var attempt = await TestData.AddPaymentAttemptAsync(_factory, invoice.Id, 1000m);
        var json = Completed(attempt, 100000, StripeTestHelpers.NewPaymentIntentId(), eventId: StripeWebhookBuilder.NewEventId());

        var first = await StripeTestHelpers.SendWebhookAsync(_factory, json);
        var second = await StripeTestHelpers.SendWebhookAsync(_factory, json);

        await first.AssertStatusAsync(HttpStatusCode.OK);
        await second.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Equal("Ye event pehle process ho chuka hai.", await second.ReadMessageAsync());
        Assert.Single(await StripeTestHelpers.GetPaymentsAsync(_factory, invoice.Id));
        Assert.Single(await StripeTestHelpers.GetAuditAsync(_factory, "PaymentSucceeded", invoice.Id.ToString()));
    }

    [Fact]
    public async Task Second_success_event_for_same_session_is_ignored()
    {
        var (invoice, attempt, paymentIntentId) = await StripeTestHelpers.CreateSucceededAttemptAsync(_factory);

        // Naya event id (jaise async_payment_succeeded bhi aa jaye)
        var json = StripeWebhookBuilder.CheckoutSessionEvent(
            "checkout.session.async_payment_succeeded", attempt.ProviderSessionId, 100000, "PKR",
            paymentIntentId: paymentIntentId);
        var response = await StripeTestHelpers.SendWebhookAsync(_factory, json);

        await response.AssertStatusAsync(HttpStatusCode.OK);
        Assert.StartsWith("Ignored", await response.ReadMessageAsync());
        Assert.Single(await StripeTestHelpers.GetPaymentsAsync(_factory, invoice.Id));
    }

    [Fact]
    public async Task Completed_but_unpaid_session_records_nothing()
    {
        var invoice = await StripeTestHelpers.NewInvoiceAsync(_factory);
        var attempt = await TestData.AddPaymentAttemptAsync(_factory, invoice.Id, 1000m);

        var response = await StripeTestHelpers.SendWebhookAsync(
            _factory, Completed(attempt, 100000, StripeTestHelpers.NewPaymentIntentId(), paymentStatus: "unpaid"));

        await response.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Empty(await StripeTestHelpers.GetPaymentsAsync(_factory, invoice.Id));
        Assert.Equal(PaymentAttemptStatus.Pending, (await StripeTestHelpers.GetAttemptAsync(_factory, attempt.Id)).Status);
        Assert.Equal(InvoiceStatus.Approved, (await StripeTestHelpers.GetInvoiceAsync(_factory, invoice.Id)).Status);
    }

    [Fact]
    public async Task Async_payment_succeeded_after_unpaid_completion_records_payment()
    {
        var invoice = await StripeTestHelpers.NewInvoiceAsync(_factory);
        var attempt = await TestData.AddPaymentAttemptAsync(_factory, invoice.Id, 1000m);
        var paymentIntentId = StripeTestHelpers.NewPaymentIntentId();

        var unpaid = await StripeTestHelpers.SendWebhookAsync(
            _factory, Completed(attempt, 100000, paymentIntentId, paymentStatus: "unpaid"));
        await unpaid.AssertStatusAsync(HttpStatusCode.OK);

        var json = StripeWebhookBuilder.CheckoutSessionEvent(
            "checkout.session.async_payment_succeeded", attempt.ProviderSessionId, 100000, "PKR",
            paymentIntentId: paymentIntentId);
        var response = await StripeTestHelpers.SendWebhookAsync(_factory, json);

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var payment = Assert.Single(await StripeTestHelpers.GetPaymentsAsync(_factory, invoice.Id));
        Assert.Equal(1000m, payment.Amount);
        Assert.Equal(attempt.Id, payment.PaymentAttemptId);
        Assert.Equal(PaymentAttemptStatus.Succeeded, (await StripeTestHelpers.GetAttemptAsync(_factory, attempt.Id)).Status);
        Assert.Equal(InvoiceStatus.Paid, (await StripeTestHelpers.GetInvoiceAsync(_factory, invoice.Id)).Status);
    }

    [Fact]
    public async Task Payment_on_replaced_expired_session_is_still_recorded()
    {
        var invoice = await StripeTestHelpers.NewInvoiceAsync(_factory);
        var attempt = await TestData.AddPaymentAttemptAsync(
            _factory, invoice.Id, 1000m, status: PaymentAttemptStatus.Expired);

        var response = await StripeTestHelpers.SendWebhookAsync(
            _factory, Completed(attempt, 100000, StripeTestHelpers.NewPaymentIntentId()));

        await response.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Single(await StripeTestHelpers.GetPaymentsAsync(_factory, invoice.Id));
        Assert.Equal(PaymentAttemptStatus.Succeeded, (await StripeTestHelpers.GetAttemptAsync(_factory, attempt.Id)).Status);
    }

    // ---------- NeedsReview ----------

    [Fact]
    public async Task Amount_mismatch_moves_attempt_to_needs_review_without_payment()
    {
        var invoice = await StripeTestHelpers.NewInvoiceAsync(_factory);
        var attempt = await TestData.AddPaymentAttemptAsync(_factory, invoice.Id, 1000m);
        var paymentIntentId = StripeTestHelpers.NewPaymentIntentId();

        var response = await StripeTestHelpers.SendWebhookAsync(_factory, Completed(attempt, 99900, paymentIntentId));

        await response.AssertStatusAsync(HttpStatusCode.OK);
        Assert.StartsWith("NeedsReview", await response.ReadMessageAsync());
        Assert.Empty(await StripeTestHelpers.GetPaymentsAsync(_factory, invoice.Id));
        Assert.Equal(InvoiceStatus.Approved, (await StripeTestHelpers.GetInvoiceAsync(_factory, invoice.Id)).Status);

        var saved = await StripeTestHelpers.GetAttemptAsync(_factory, attempt.Id);
        Assert.Equal(PaymentAttemptStatus.NeedsReview, saved.Status);
        Assert.False(string.IsNullOrWhiteSpace(saved.FailureReason));
        Assert.Equal(paymentIntentId, saved.ProviderPaymentIntentId);
        Assert.Single(await StripeTestHelpers.GetAuditAsync(_factory, "PaymentNeedsReview", invoice.Id.ToString()));
    }

    [Fact]
    public async Task Currency_mismatch_moves_attempt_to_needs_review()
    {
        var invoice = await StripeTestHelpers.NewInvoiceAsync(_factory);
        var attempt = await TestData.AddPaymentAttemptAsync(_factory, invoice.Id, 1000m);

        var response = await StripeTestHelpers.SendWebhookAsync(
            _factory, Completed(attempt, 100000, StripeTestHelpers.NewPaymentIntentId(), currency: "USD"));

        await response.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Empty(await StripeTestHelpers.GetPaymentsAsync(_factory, invoice.Id));
        Assert.Equal(PaymentAttemptStatus.NeedsReview, (await StripeTestHelpers.GetAttemptAsync(_factory, attempt.Id)).Status);
    }

    [Fact]
    public async Task Invoice_paid_manually_before_webhook_moves_attempt_to_needs_review()
    {
        var invoice = await StripeTestHelpers.NewInvoiceAsync(_factory, subtotal: 1000m);
        var attempt = await TestData.AddPaymentAttemptAsync(_factory, invoice.Id, 1000m);
        await TestData.AddPaymentAsync(_factory, invoice.Id, 1000m);
        await StripeTestHelpers.SetInvoiceStatusAsync(_factory, invoice.Id, InvoiceStatus.Paid);
        var paymentIntentId = StripeTestHelpers.NewPaymentIntentId();

        var response = await StripeTestHelpers.SendWebhookAsync(_factory, Completed(attempt, 100000, paymentIntentId));

        await response.AssertStatusAsync(HttpStatusCode.OK);
        Assert.StartsWith("NeedsReview", await response.ReadMessageAsync());

        // Sirf manual payment, Stripe wali darj nahi hui
        var payment = Assert.Single(await StripeTestHelpers.GetPaymentsAsync(_factory, invoice.Id));
        Assert.Null(payment.PaymentAttemptId);

        var saved = await StripeTestHelpers.GetAttemptAsync(_factory, attempt.Id);
        Assert.Equal(PaymentAttemptStatus.NeedsReview, saved.Status);
        Assert.Equal(paymentIntentId, saved.ProviderPaymentIntentId);
        Assert.Equal(InvoiceStatus.Paid, (await StripeTestHelpers.GetInvoiceAsync(_factory, invoice.Id)).Status);
    }

    [Fact]
    public async Task Payment_larger_than_remaining_balance_moves_attempt_to_needs_review()
    {
        var invoice = await StripeTestHelpers.NewInvoiceAsync(_factory, InvoiceStatus.Partial, subtotal: 1000m);
        var attempt = await TestData.AddPaymentAttemptAsync(_factory, invoice.Id, 1000m);
        await TestData.AddPaymentAsync(_factory, invoice.Id, 300m);

        var response = await StripeTestHelpers.SendWebhookAsync(
            _factory, Completed(attempt, 100000, StripeTestHelpers.NewPaymentIntentId()));

        await response.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Equal(PaymentAttemptStatus.NeedsReview, (await StripeTestHelpers.GetAttemptAsync(_factory, attempt.Id)).Status);
        Assert.Single(await StripeTestHelpers.GetPaymentsAsync(_factory, invoice.Id));
    }

    [Fact]
    public async Task Success_after_partial_refund_of_unbooked_attempt_moves_to_needs_review()
    {
        // Refund ka event payment darj hone se pehle aa gaya (attempt abhi Pending)
        var invoice = await StripeTestHelpers.NewInvoiceAsync(_factory, subtotal: 1000m);
        var paymentIntentId = StripeTestHelpers.NewPaymentIntentId();
        var attempt = await TestData.AddPaymentAttemptAsync(_factory, invoice.Id, 1000m, paymentIntentId: paymentIntentId);

        var refund = await StripeTestHelpers.SendWebhookAsync(
            _factory, StripeWebhookBuilder.ChargeRefundedEvent(paymentIntentId, 100000, 40000, "PKR"));
        await refund.AssertStatusAsync(HttpStatusCode.OK);

        var afterRefund = await StripeTestHelpers.GetAttemptAsync(_factory, attempt.Id);
        Assert.Equal(PaymentAttemptStatus.Pending, afterRefund.Status);
        Assert.Equal(400m, afterRefund.RefundedAmount);
        Assert.Empty(await StripeTestHelpers.GetPaymentsAsync(_factory, invoice.Id));

        var response = await StripeTestHelpers.SendWebhookAsync(_factory, Completed(attempt, 100000, paymentIntentId));

        await response.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Equal("NeedsReview: pehle se refund.", await response.ReadMessageAsync());

        // Poori 1000 darj nahi hui: 400 pehle hi wapis ja chuke the
        Assert.Empty(await StripeTestHelpers.GetPaymentsAsync(_factory, invoice.Id));
        Assert.Equal(InvoiceStatus.Approved, (await StripeTestHelpers.GetInvoiceAsync(_factory, invoice.Id)).Status);

        var saved = await StripeTestHelpers.GetAttemptAsync(_factory, attempt.Id);
        Assert.Equal(PaymentAttemptStatus.NeedsReview, saved.Status);
        Assert.Equal(400m, saved.RefundedAmount);
        Assert.Equal(paymentIntentId, saved.ProviderPaymentIntentId);
        Assert.False(string.IsNullOrWhiteSpace(saved.FailureReason));
        Assert.Single(await StripeTestHelpers.GetAuditAsync(_factory, "PaymentNeedsReview", invoice.Id.ToString()));
        Assert.Empty(await StripeTestHelpers.GetAuditAsync(_factory, "PaymentSucceeded", invoice.Id.ToString()));
    }

    [Fact]
    public async Task Success_after_full_refund_of_unbooked_attempt_is_ignored()
    {
        var invoice = await StripeTestHelpers.NewInvoiceAsync(_factory, subtotal: 1000m);
        var paymentIntentId = StripeTestHelpers.NewPaymentIntentId();
        var attempt = await TestData.AddPaymentAttemptAsync(_factory, invoice.Id, 1000m, paymentIntentId: paymentIntentId);

        var refund = await StripeTestHelpers.SendWebhookAsync(
            _factory, StripeWebhookBuilder.ChargeRefundedEvent(paymentIntentId, 100000, 100000, "PKR"));
        await refund.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Equal(PaymentAttemptStatus.Refunded, (await StripeTestHelpers.GetAttemptAsync(_factory, attempt.Id)).Status);

        var response = await StripeTestHelpers.SendWebhookAsync(_factory, Completed(attempt, 100000, paymentIntentId));

        await response.AssertStatusAsync(HttpStatusCode.OK);
        Assert.StartsWith("Ignored", await response.ReadMessageAsync());
        Assert.Empty(await StripeTestHelpers.GetPaymentsAsync(_factory, invoice.Id));
        Assert.Equal(InvoiceStatus.Approved, (await StripeTestHelpers.GetInvoiceAsync(_factory, invoice.Id)).Status);
        Assert.Equal(PaymentAttemptStatus.Refunded, (await StripeTestHelpers.GetAttemptAsync(_factory, attempt.Id)).Status);
    }

    // ---------- Failed / expired ----------

    [Fact]
    public async Task Expired_event_marks_pending_attempt_expired()
    {
        var invoice = await StripeTestHelpers.NewInvoiceAsync(_factory);
        var attempt = await TestData.AddPaymentAttemptAsync(_factory, invoice.Id, 1000m);
        var json = StripeWebhookBuilder.CheckoutSessionEvent(
            "checkout.session.expired", attempt.ProviderSessionId, 100000, "PKR",
            paymentStatus: "unpaid", paymentIntentId: null);

        var response = await StripeTestHelpers.SendWebhookAsync(_factory, json);

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var saved = await StripeTestHelpers.GetAttemptAsync(_factory, attempt.Id);
        Assert.Equal(PaymentAttemptStatus.Expired, saved.Status);
        Assert.Equal("Session expire ho gaya.", saved.FailureReason);
        Assert.NotNull(saved.CompletedAtUtc);
        Assert.Empty(await StripeTestHelpers.GetPaymentsAsync(_factory, invoice.Id));
        Assert.Single(await StripeTestHelpers.GetAuditAsync(_factory, "PaymentFailed", invoice.Id.ToString()));
    }

    [Fact]
    public async Task Async_payment_failed_event_marks_attempt_failed()
    {
        var invoice = await StripeTestHelpers.NewInvoiceAsync(_factory);
        var attempt = await TestData.AddPaymentAttemptAsync(_factory, invoice.Id, 1000m);
        var json = StripeWebhookBuilder.CheckoutSessionEvent(
            "checkout.session.async_payment_failed", attempt.ProviderSessionId, 100000, "PKR",
            paymentStatus: "unpaid");

        var response = await StripeTestHelpers.SendWebhookAsync(_factory, json);

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var saved = await StripeTestHelpers.GetAttemptAsync(_factory, attempt.Id);
        Assert.Equal(PaymentAttemptStatus.Failed, saved.Status);
        Assert.Equal("Payment fail hui.", saved.FailureReason);
        Assert.Empty(await StripeTestHelpers.GetPaymentsAsync(_factory, invoice.Id));
    }

    [Fact]
    public async Task Expired_event_after_success_keeps_attempt_succeeded()
    {
        var (invoice, attempt, _) = await StripeTestHelpers.CreateSucceededAttemptAsync(_factory);
        var json = StripeWebhookBuilder.CheckoutSessionEvent(
            "checkout.session.expired", attempt.ProviderSessionId, 100000, "PKR", paymentStatus: "unpaid");

        var response = await StripeTestHelpers.SendWebhookAsync(_factory, json);

        await response.AssertStatusAsync(HttpStatusCode.OK);
        Assert.StartsWith("Ignored", await response.ReadMessageAsync());
        Assert.Equal(PaymentAttemptStatus.Succeeded, (await StripeTestHelpers.GetAttemptAsync(_factory, attempt.Id)).Status);
        Assert.Single(await StripeTestHelpers.GetPaymentsAsync(_factory, invoice.Id));
        Assert.Equal(InvoiceStatus.Paid, (await StripeTestHelpers.GetInvoiceAsync(_factory, invoice.Id)).Status);
    }

    // ---------- Unmatched / ignored ----------

    [Fact]
    public async Task Unknown_session_is_acknowledged_as_unmatched()
    {
        var json = StripeWebhookBuilder.CheckoutSessionEvent(
            "checkout.session.completed", $"cs_test_unknown_{Guid.NewGuid():N}", 100000, "PKR");

        var response = await StripeTestHelpers.SendWebhookAsync(_factory, json);

        await response.AssertStatusAsync(HttpStatusCode.OK);
        Assert.StartsWith("Unmatched", await response.ReadMessageAsync());
    }

    [Fact]
    public async Task Unhandled_event_type_is_ignored()
    {
        var invoice = await StripeTestHelpers.NewInvoiceAsync(_factory);
        var attempt = await TestData.AddPaymentAttemptAsync(_factory, invoice.Id, 1000m);
        var json = StripeWebhookBuilder.CheckoutSessionEvent(
            "checkout.session.something_new", attempt.ProviderSessionId, 100000, "PKR");

        var response = await StripeTestHelpers.SendWebhookAsync(_factory, json);

        await response.AssertStatusAsync(HttpStatusCode.OK);
        Assert.StartsWith("Ignored", await response.ReadMessageAsync());
        Assert.Equal(PaymentAttemptStatus.Pending, (await StripeTestHelpers.GetAttemptAsync(_factory, attempt.Id)).Status);
        Assert.Empty(await StripeTestHelpers.GetPaymentsAsync(_factory, invoice.Id));
    }
}
