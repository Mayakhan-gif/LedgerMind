using System.Net;
using System.Net.Http.Json;
using LedgerMind.Models;
using LedgerMind.Tests.Infrastructure;

namespace LedgerMind.Tests.StripePayments;

public class StripeRefundApiTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public StripeRefundApiTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private bool StripeRefundSent(string paymentIntentId) =>
        _factory.FakeStripe.Refunds.Any(r => r.PaymentIntentId == paymentIntentId);

    [Fact]
    public async Task Admin_refund_reverses_payment_and_recalculates_invoice()
    {
        var client = await TestAuth.AdminClientAsync(_factory);
        var adminId = await TestAuth.GetAdminIdAsync(_factory);
        var (invoice, attempt, paymentIntentId) = await StripeTestHelpers.CreateSucceededAttemptAsync(
            _factory, invoiceTotal: 1000m, alreadyPaid: 400m);
        Assert.Equal(InvoiceStatus.Paid, (await StripeTestHelpers.GetInvoiceAsync(_factory, invoice.Id)).Status);

        var response = await client.PostAsJsonAsync(StripeTestHelpers.RefundUrl(attempt.Id),
            new { note = "Customer ne cancel kiya" });

        await response.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Contains(_factory.FakeStripe.Refunds,
            r => r.PaymentIntentId == paymentIntentId && r.AmountMinor == 60000);

        var json = await response.ReadJsonAsync();
        Assert.Equal(PaymentAttemptStatus.Refunded, json.GetProperty("status").GetString());
        Assert.Equal(600m, json.GetProperty("refundedAmount").GetDecimal());

        var reversal = Assert.Single(await StripeTestHelpers.GetPaymentsAsync(_factory, invoice.Id), p => p.Amount < 0);
        Assert.Equal(-600m, reversal.Amount);
        Assert.Equal("Stripe refund", reversal.Method);
        Assert.Equal(attempt.Id, reversal.PaymentAttemptId);
        Assert.Equal(adminId, reversal.CreatedByUserId);

        // 400 manual payment baqi, is liye Partial
        Assert.Equal(InvoiceStatus.Partial, (await StripeTestHelpers.GetInvoiceAsync(_factory, invoice.Id)).Status);

        var saved = await StripeTestHelpers.GetAttemptAsync(_factory, attempt.Id);
        Assert.Equal(PaymentAttemptStatus.Refunded, saved.Status);
        Assert.Equal(600m, saved.RefundedAmount);
        Assert.StartsWith("re_test_", saved.ProviderRefundId);

        var audit = Assert.Single(await StripeTestHelpers.GetAuditAsync(_factory, "PaymentRefunded", invoice.Id.ToString()));
        Assert.Equal(adminId, audit.UserId);
    }

    [Fact]
    public async Task Refund_sends_an_idempotency_key_tied_to_the_attempt_and_refunded_amount()
    {
        var client = await TestAuth.AdminClientAsync(_factory);
        var (_, attempt, _) = await StripeTestHelpers.CreateSucceededAttemptAsync(
            _factory, invoiceTotal: 1000m, alreadyPaid: 0m);

        var response = await client.PostAsJsonAsync(StripeTestHelpers.RefundUrl(attempt.Id), new { note = "Twice clicked" });

        await response.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Contains(_factory.FakeStripe.RefundIdempotencyKeys, k => k == $"refund-attempt-{attempt.Id}-0");
    }

    [Fact]
    public async Task Full_refund_of_only_payment_returns_invoice_to_approved()
    {
        var client = await TestAuth.AdminClientAsync(_factory);
        var (invoice, attempt, paymentIntentId) = await StripeTestHelpers.CreateSucceededAttemptAsync(_factory);

        var response = await client.PostAsJsonAsync(StripeTestHelpers.RefundUrl(attempt.Id), new { note = (string?)null });

        await response.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Contains(_factory.FakeStripe.Refunds,
            r => r.PaymentIntentId == paymentIntentId && r.AmountMinor == 100000);
        Assert.Equal(0m, (await StripeTestHelpers.GetPaymentsAsync(_factory, invoice.Id)).Sum(p => p.Amount));
        Assert.Equal(InvoiceStatus.Approved, (await StripeTestHelpers.GetInvoiceAsync(_factory, invoice.Id)).Status);
    }

    [Fact]
    public async Task Finance_manager_cannot_refund()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var (invoice, attempt, paymentIntentId) = await StripeTestHelpers.CreateSucceededAttemptAsync(_factory);

        var response = await client.PostAsJsonAsync(StripeTestHelpers.RefundUrl(attempt.Id), new { note = "x" });

        await response.AssertStatusAsync(HttpStatusCode.Forbidden);
        Assert.False(StripeRefundSent(paymentIntentId));
        Assert.Equal(PaymentAttemptStatus.Succeeded, (await StripeTestHelpers.GetAttemptAsync(_factory, attempt.Id)).Status);
        Assert.Single(await StripeTestHelpers.GetPaymentsAsync(_factory, invoice.Id));
    }

    [Fact]
    public async Task Refunding_twice_returns_400_and_calls_stripe_once()
    {
        var client = await TestAuth.AdminClientAsync(_factory);
        var (invoice, attempt, paymentIntentId) = await StripeTestHelpers.CreateSucceededAttemptAsync(_factory);

        var first = await client.PostAsJsonAsync(StripeTestHelpers.RefundUrl(attempt.Id), new { note = "pehla" });
        await first.AssertStatusAsync(HttpStatusCode.OK);
        var second = await client.PostAsJsonAsync(StripeTestHelpers.RefundUrl(attempt.Id), new { note = "doosra" });

        await second.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Single(_factory.FakeStripe.Refunds, r => r.PaymentIntentId == paymentIntentId);
        Assert.Single(await StripeTestHelpers.GetPaymentsAsync(_factory, invoice.Id), p => p.Amount < 0);
    }

    [Fact]
    public async Task Refunding_needs_review_attempt_returns_everything_stripe_took_and_adds_no_ledger_entry()
    {
        // Rs 1000 ka attempt, Stripe ne 1200 liye (mismatch): refund poore 1200 ka, sirf 1000 ka nahi
        var client = await TestAuth.AdminClientAsync(_factory);
        var (invoice, attempt, paymentIntentId) = await StripeTestHelpers.CreateNeedsReviewAttemptAsync(
            _factory, invoiceTotal: 1000m, chargedMinor: 120000);

        var response = await client.PostAsJsonAsync(StripeTestHelpers.RefundUrl(attempt.Id), new { note = "ghalat raqam" });

        await response.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Contains(_factory.FakeStripe.Refunds,
            r => r.PaymentIntentId == paymentIntentId && r.AmountMinor == 120000);
        Assert.Empty(await StripeTestHelpers.GetPaymentsAsync(_factory, invoice.Id));
        Assert.Equal(InvoiceStatus.Approved, (await StripeTestHelpers.GetInvoiceAsync(_factory, invoice.Id)).Status);

        var saved = await StripeTestHelpers.GetAttemptAsync(_factory, attempt.Id);
        Assert.Equal(PaymentAttemptStatus.Refunded, saved.Status);
        Assert.Equal(1200m, saved.RefundedAmount);
    }

    [Fact]
    public async Task Charge_refunded_webhook_after_api_refund_is_ignored()
    {
        var client = await TestAuth.AdminClientAsync(_factory);
        var (invoice, attempt, paymentIntentId) = await StripeTestHelpers.CreateSucceededAttemptAsync(_factory);

        var refund = await client.PostAsJsonAsync(StripeTestHelpers.RefundUrl(attempt.Id), new { note = "api" });
        await refund.AssertStatusAsync(HttpStatusCode.OK);

        var json = StripeWebhookBuilder.ChargeRefundedEvent(paymentIntentId, 100000, 100000, "PKR");
        var webhook = await StripeTestHelpers.SendWebhookAsync(_factory, json);

        await webhook.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Equal("Ignored: refund pehle se darj.", await webhook.ReadMessageAsync());

        var payments = await StripeTestHelpers.GetPaymentsAsync(_factory, invoice.Id);
        Assert.Equal(2, payments.Count);
        Assert.Equal(0m, payments.Sum(p => p.Amount));
        Assert.Equal(PaymentAttemptStatus.Refunded, (await StripeTestHelpers.GetAttemptAsync(_factory, attempt.Id)).Status);
    }

    [Fact]
    public async Task Refund_after_partial_webhook_refund_returns_only_the_rest()
    {
        var client = await TestAuth.AdminClientAsync(_factory);
        var (invoice, attempt, paymentIntentId) = await StripeTestHelpers.CreateSucceededAttemptAsync(_factory);

        var partial = await StripeTestHelpers.SendWebhookAsync(
            _factory, StripeWebhookBuilder.ChargeRefundedEvent(paymentIntentId, 100000, 25000, "PKR"));
        await partial.AssertStatusAsync(HttpStatusCode.OK);

        var response = await client.PostAsJsonAsync(StripeTestHelpers.RefundUrl(attempt.Id), new { note = "baqi" });

        await response.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Contains(_factory.FakeStripe.Refunds,
            r => r.PaymentIntentId == paymentIntentId && r.AmountMinor == 75000);

        var reversals = (await StripeTestHelpers.GetPaymentsAsync(_factory, invoice.Id))
            .Where(p => p.Amount < 0)
            .Select(p => p.Amount)
            .ToList();
        Assert.Equal(new[] { -250m, -750m }, reversals);

        var saved = await StripeTestHelpers.GetAttemptAsync(_factory, attempt.Id);
        Assert.Equal(PaymentAttemptStatus.Refunded, saved.Status);
        Assert.Equal(1000m, saved.RefundedAmount);
    }

    [Theory]
    [InlineData(PaymentAttemptStatus.Pending)]
    [InlineData(PaymentAttemptStatus.Expired)]
    [InlineData(PaymentAttemptStatus.Failed)]
    [InlineData(PaymentAttemptStatus.Resolved)]
    public async Task Refund_of_attempt_in_wrong_status_returns_400(string status)
    {
        var client = await TestAuth.AdminClientAsync(_factory);
        var invoice = await StripeTestHelpers.NewInvoiceAsync(_factory);
        var paymentIntentId = StripeTestHelpers.NewPaymentIntentId();
        var attempt = await TestData.AddPaymentAttemptAsync(
            _factory, invoice.Id, 1000m, status: status, paymentIntentId: paymentIntentId);

        var response = await client.PostAsJsonAsync(StripeTestHelpers.RefundUrl(attempt.Id), new { note = "x" });

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.False(StripeRefundSent(paymentIntentId));
        Assert.Equal(status, (await StripeTestHelpers.GetAttemptAsync(_factory, attempt.Id)).Status);
    }

    [Fact]
    public async Task Refund_without_payment_intent_returns_400()
    {
        var client = await TestAuth.AdminClientAsync(_factory);
        var invoice = await StripeTestHelpers.NewInvoiceAsync(_factory);
        var attempt = await TestData.AddPaymentAttemptAsync(
            _factory, invoice.Id, 1000m, status: PaymentAttemptStatus.NeedsReview);

        var response = await client.PostAsJsonAsync(StripeTestHelpers.RefundUrl(attempt.Id), new { note = "x" });

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Equal(PaymentAttemptStatus.NeedsReview, (await StripeTestHelpers.GetAttemptAsync(_factory, attempt.Id)).Status);
    }

    [Fact]
    public async Task Refund_rejected_by_stripe_returns_502_and_changes_nothing()
    {
        var client = await TestAuth.AdminClientAsync(_factory);
        var (invoice, attempt, _) = await StripeTestHelpers.CreateSucceededAttemptAsync(_factory);

        _factory.FakeStripe.RefundStatus = "failed";
        HttpResponseMessage response;
        try
        {
            response = await client.PostAsJsonAsync(StripeTestHelpers.RefundUrl(attempt.Id), new { note = "x" });
        }
        finally
        {
            _factory.FakeStripe.RefundStatus = "succeeded";
        }

        await response.AssertStatusAsync(HttpStatusCode.BadGateway);
        var saved = await StripeTestHelpers.GetAttemptAsync(_factory, attempt.Id);
        Assert.Equal(PaymentAttemptStatus.Succeeded, saved.Status);
        Assert.Equal(0m, saved.RefundedAmount);
        Assert.Single(await StripeTestHelpers.GetPaymentsAsync(_factory, invoice.Id));
        Assert.Equal(InvoiceStatus.Paid, (await StripeTestHelpers.GetInvoiceAsync(_factory, invoice.Id)).Status);
    }

    [Fact]
    public async Task Stripe_error_during_refund_returns_502()
    {
        var client = await TestAuth.AdminClientAsync(_factory);
        var (invoice, attempt, _) = await StripeTestHelpers.CreateSucceededAttemptAsync(_factory);

        _factory.FakeStripe.ThrowOnRefund = new global::Stripe.StripeException("charge already refunded");
        HttpResponseMessage response;
        try
        {
            response = await client.PostAsJsonAsync(StripeTestHelpers.RefundUrl(attempt.Id), new { note = "x" });
        }
        finally
        {
            _factory.FakeStripe.ThrowOnRefund = null;
        }

        await response.AssertStatusAsync(HttpStatusCode.BadGateway);
        Assert.Equal(PaymentAttemptStatus.Succeeded, (await StripeTestHelpers.GetAttemptAsync(_factory, attempt.Id)).Status);
        Assert.Single(await StripeTestHelpers.GetPaymentsAsync(_factory, invoice.Id));
    }

    [Fact]
    public async Task Refund_unknown_attempt_returns_404()
    {
        var client = await TestAuth.AdminClientAsync(_factory);

        var response = await client.PostAsJsonAsync(StripeTestHelpers.RefundUrl(987654321), new { note = "x" });

        await response.AssertStatusAsync(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Refund_with_too_long_note_returns_400()
    {
        var client = await TestAuth.AdminClientAsync(_factory);
        var (_, attempt, paymentIntentId) = await StripeTestHelpers.CreateSucceededAttemptAsync(_factory);

        var response = await client.PostAsJsonAsync(StripeTestHelpers.RefundUrl(attempt.Id),
            new { note = new string('x', 501) });

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.False(StripeRefundSent(paymentIntentId));
    }
}
