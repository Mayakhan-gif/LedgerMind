using System.Net;
using LedgerMind.Models;
using LedgerMind.Tests.Infrastructure;

namespace LedgerMind.Tests.StripePayments;

public class StripeRefundWebhookTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public StripeRefundWebhookTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static string Refunded(string paymentIntentId, long amountMinor, long refundedMinor) =>
        StripeWebhookBuilder.ChargeRefundedEvent(paymentIntentId, amountMinor, refundedMinor, "PKR");

    [Fact]
    public async Task Full_refund_adds_negative_reversal_and_reopens_invoice()
    {
        var (invoice, attempt, paymentIntentId) = await StripeTestHelpers.CreateSucceededAttemptAsync(_factory);

        var response = await StripeTestHelpers.SendWebhookAsync(_factory, Refunded(paymentIntentId, 100000, 100000));

        await response.AssertStatusAsync(HttpStatusCode.OK);
        Assert.StartsWith("Refunded", await response.ReadMessageAsync());

        var payments = await StripeTestHelpers.GetPaymentsAsync(_factory, invoice.Id);
        Assert.Equal(2, payments.Count);
        var reversal = Assert.Single(payments, p => p.Amount < 0);
        Assert.Equal(-1000m, reversal.Amount);
        Assert.Equal("Stripe refund", reversal.Method);
        Assert.Equal(attempt.Id, reversal.PaymentAttemptId);
        Assert.Equal(0m, payments.Sum(p => p.Amount));

        Assert.Equal(InvoiceStatus.Approved, (await StripeTestHelpers.GetInvoiceAsync(_factory, invoice.Id)).Status);

        var saved = await StripeTestHelpers.GetAttemptAsync(_factory, attempt.Id);
        Assert.Equal(PaymentAttemptStatus.Refunded, saved.Status);
        Assert.Equal(1000m, saved.RefundedAmount);

        Assert.Single(await StripeTestHelpers.GetAuditAsync(_factory, "PaymentRefunded", invoice.Id.ToString()));
    }

    [Fact]
    public async Task Partial_then_full_refund_events_add_two_reversals_totalling_the_amount()
    {
        var (invoice, attempt, paymentIntentId) = await StripeTestHelpers.CreateSucceededAttemptAsync(_factory);

        var partial = await StripeTestHelpers.SendWebhookAsync(_factory, Refunded(paymentIntentId, 100000, 30000));
        await partial.AssertStatusAsync(HttpStatusCode.OK);

        var afterPartial = await StripeTestHelpers.GetAttemptAsync(_factory, attempt.Id);
        Assert.Equal(PaymentAttemptStatus.Succeeded, afterPartial.Status);
        Assert.Equal(300m, afterPartial.RefundedAmount);
        Assert.Equal(InvoiceStatus.Partial, (await StripeTestHelpers.GetInvoiceAsync(_factory, invoice.Id)).Status);

        var full = await StripeTestHelpers.SendWebhookAsync(_factory, Refunded(paymentIntentId, 100000, 100000));
        await full.AssertStatusAsync(HttpStatusCode.OK);

        var reversals = (await StripeTestHelpers.GetPaymentsAsync(_factory, invoice.Id))
            .Where(p => p.Amount < 0)
            .Select(p => p.Amount)
            .ToList();
        Assert.Equal(new[] { -300m, -700m }, reversals);
        Assert.Equal(-1000m, reversals.Sum());

        var saved = await StripeTestHelpers.GetAttemptAsync(_factory, attempt.Id);
        Assert.Equal(PaymentAttemptStatus.Refunded, saved.Status);
        Assert.Equal(1000m, saved.RefundedAmount);
        Assert.Equal(InvoiceStatus.Approved, (await StripeTestHelpers.GetInvoiceAsync(_factory, invoice.Id)).Status);
    }

    [Fact]
    public async Task Replayed_refund_with_new_event_id_adds_no_extra_reversal()
    {
        var (invoice, _, paymentIntentId) = await StripeTestHelpers.CreateSucceededAttemptAsync(_factory);

        var first = await StripeTestHelpers.SendWebhookAsync(_factory, Refunded(paymentIntentId, 100000, 40000));
        await first.AssertStatusAsync(HttpStatusCode.OK);

        // Naya event id magar wahi kul amount_refunded
        var replay = await StripeTestHelpers.SendWebhookAsync(_factory, Refunded(paymentIntentId, 100000, 40000));

        await replay.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Equal("Ignored: refund pehle se darj.", await replay.ReadMessageAsync());
        var reversal = Assert.Single(await StripeTestHelpers.GetPaymentsAsync(_factory, invoice.Id), p => p.Amount < 0);
        Assert.Equal(-400m, reversal.Amount);
        Assert.Single(await StripeTestHelpers.GetAuditAsync(_factory, "PaymentRefunded", invoice.Id.ToString()));
    }

    [Fact]
    public async Task Refund_for_partially_paid_invoice_returns_it_to_partial()
    {
        var (invoice, attempt, paymentIntentId) = await StripeTestHelpers.CreateSucceededAttemptAsync(
            _factory, invoiceTotal: 1000m, alreadyPaid: 400m);
        Assert.Equal(InvoiceStatus.Paid, (await StripeTestHelpers.GetInvoiceAsync(_factory, invoice.Id)).Status);

        var response = await StripeTestHelpers.SendWebhookAsync(_factory, Refunded(paymentIntentId, 60000, 60000));

        await response.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Equal(InvoiceStatus.Partial, (await StripeTestHelpers.GetInvoiceAsync(_factory, invoice.Id)).Status);
        Assert.Equal(400m, (await StripeTestHelpers.GetPaymentsAsync(_factory, invoice.Id)).Sum(p => p.Amount));
        Assert.Equal(PaymentAttemptStatus.Refunded, (await StripeTestHelpers.GetAttemptAsync(_factory, attempt.Id)).Status);
    }

    [Fact]
    public async Task Refund_of_needs_review_payment_adds_no_ledger_entry()
    {
        // Rs 1000 ka attempt, Stripe ne 1200 liye (mismatch), phir poora 1200 wapis
        var (invoice, attempt, paymentIntentId) = await StripeTestHelpers.CreateNeedsReviewAttemptAsync(
            _factory, invoiceTotal: 1000m, chargedMinor: 120000);

        var response = await StripeTestHelpers.SendWebhookAsync(_factory, Refunded(paymentIntentId, 120000, 120000));

        await response.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Empty(await StripeTestHelpers.GetPaymentsAsync(_factory, invoice.Id));
        Assert.Equal(InvoiceStatus.Approved, (await StripeTestHelpers.GetInvoiceAsync(_factory, invoice.Id)).Status);

        var saved = await StripeTestHelpers.GetAttemptAsync(_factory, attempt.Id);
        Assert.Equal(PaymentAttemptStatus.Refunded, saved.Status);
        Assert.Equal(1200m, saved.RefundedAmount);
    }

    [Fact]
    public async Task Refund_for_unknown_payment_intent_is_unmatched()
    {
        var response = await StripeTestHelpers.SendWebhookAsync(
            _factory, Refunded(StripeTestHelpers.NewPaymentIntentId(), 100000, 100000));

        await response.AssertStatusAsync(HttpStatusCode.OK);
        Assert.StartsWith("Unmatched", await response.ReadMessageAsync());
    }
}
