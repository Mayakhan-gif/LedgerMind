using System.Net;
using System.Net.Http.Json;
using LedgerMind.Models;
using LedgerMind.Tests.Infrastructure;

namespace LedgerMind.Tests.StripePayments;

public class StripeCheckoutTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public StripeCheckoutTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Checkout_returns_url_and_creates_pending_attempt_for_the_balance()
    {
        var (manager, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var invoice = await StripeTestHelpers.NewInvoiceAsync(_factory, InvoiceStatus.Partial, subtotal: 1000m);
        await TestData.AddPaymentAsync(_factory, invoice.Id, 400m);

        var response = await client.PostAsync(StripeTestHelpers.CheckoutUrl(invoice.Id), null);

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var json = await response.ReadJsonAsync();

        var attempt = Assert.Single(await StripeTestHelpers.GetAttemptsForInvoiceAsync(_factory, invoice.Id));
        Assert.Equal(PaymentAttemptStatus.Pending, attempt.Status);
        Assert.Equal(600m, attempt.Amount);
        Assert.Equal("PKR", attempt.Currency);
        Assert.Equal("Stripe", attempt.Provider);
        Assert.Equal(manager.Id, attempt.CreatedByUserId);

        Assert.Equal($"https://checkout.stripe.com/c/pay/{attempt.ProviderSessionId}",
            json.GetProperty("checkoutUrl").GetString());
        Assert.Equal(attempt.Id, json.GetProperty("attemptId").GetInt32());
        Assert.Equal(600m, json.GetProperty("amount").GetDecimal());
        Assert.Equal("PKR", json.GetProperty("currency").GetString());
    }

    [Fact]
    public async Task Checkout_sends_balance_in_minor_units_and_invoice_details_to_stripe()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Admin);
        var invoice = await StripeTestHelpers.NewInvoiceAsync(_factory, InvoiceStatus.Partial, subtotal: 1000m);
        await TestData.AddPaymentAsync(_factory, invoice.Id, 400m);
        var before = DateTime.UtcNow;

        var response = await client.PostAsync(StripeTestHelpers.CheckoutUrl(invoice.Id), null);

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var request = Assert.Single(_factory.FakeStripe.CreatedSessions,
            r => r.ClientReferenceId == invoice.Id.ToString());

        Assert.Equal(60000L, request.AmountMinor);
        Assert.Equal("PKR", request.Currency);
        Assert.Equal("Invoice " + invoice.InvoiceNumber, request.ProductName);
        Assert.Equal("http://localhost:5173/payment/success", request.SuccessUrl);
        Assert.Equal("http://localhost:5173/payment/cancel", request.CancelUrl);
        Assert.Equal(invoice.Id.ToString(), request.Metadata["invoiceId"]);
        Assert.Equal(invoice.InvoiceNumber, request.Metadata["invoiceNumber"]);

        // Default expiry 60 minute
        Assert.InRange(request.ExpiresAtUtc, before.AddMinutes(59), DateTime.UtcNow.AddMinutes(61));
    }

    [Fact]
    public async Task Checkout_writes_payment_initiated_audit_for_the_manager()
    {
        var (manager, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var invoice = await StripeTestHelpers.NewInvoiceAsync(_factory);

        var response = await client.PostAsync(StripeTestHelpers.CheckoutUrl(invoice.Id), null);

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var audit = Assert.Single(await StripeTestHelpers.GetAuditAsync(_factory, "PaymentInitiated", invoice.Id.ToString()));
        Assert.Equal("Invoice", audit.EntityType);
        Assert.Equal(manager.Id, audit.UserId);
    }

    [Fact]
    public async Task Second_checkout_expires_the_previous_pending_attempt()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var invoice = await StripeTestHelpers.NewInvoiceAsync(_factory);

        var first = await client.PostAsync(StripeTestHelpers.CheckoutUrl(invoice.Id), null);
        await first.AssertStatusAsync(HttpStatusCode.OK);
        var second = await client.PostAsync(StripeTestHelpers.CheckoutUrl(invoice.Id), null);
        await second.AssertStatusAsync(HttpStatusCode.OK);

        var attempts = await StripeTestHelpers.GetAttemptsForInvoiceAsync(_factory, invoice.Id);
        Assert.Equal(2, attempts.Count);
        var (old, current) = (attempts[0], attempts[1]);

        Assert.Contains(old.ProviderSessionId, _factory.FakeStripe.ExpiredSessionIds);
        Assert.DoesNotContain(current.ProviderSessionId, _factory.FakeStripe.ExpiredSessionIds);

        Assert.Equal(PaymentAttemptStatus.Expired, old.Status);
        Assert.Equal("Naya checkout bana, purana band.", old.FailureReason);
        Assert.NotNull(old.CompletedAtUtc);

        Assert.Equal(PaymentAttemptStatus.Pending, current.Status);
        Assert.Equal(1000m, current.Amount);
    }

    [Fact]
    public async Task Checkout_still_works_when_stripe_cannot_expire_the_old_session()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var invoice = await StripeTestHelpers.NewInvoiceAsync(_factory);
        var old = await TestData.AddPaymentAttemptAsync(_factory, invoice.Id, 1000m);

        _factory.FakeStripe.ThrowOnExpire = new global::Stripe.StripeException("session already expired");
        HttpResponseMessage response;
        try
        {
            response = await client.PostAsync(StripeTestHelpers.CheckoutUrl(invoice.Id), null);
        }
        finally
        {
            _factory.FakeStripe.ThrowOnExpire = null;
        }

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var attempts = await StripeTestHelpers.GetAttemptsForInvoiceAsync(_factory, invoice.Id);
        Assert.Equal(2, attempts.Count);
        Assert.Equal(PaymentAttemptStatus.Expired, attempts.Single(a => a.Id == old.Id).Status);
        Assert.Equal(PaymentAttemptStatus.Pending, attempts.Single(a => a.Id != old.Id).Status);
    }

    [Fact]
    public async Task Checkout_does_not_touch_attempts_of_other_invoices()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var invoice = await StripeTestHelpers.NewInvoiceAsync(_factory);
        var otherInvoice = await StripeTestHelpers.NewInvoiceAsync(_factory);
        var otherAttempt = await TestData.AddPaymentAttemptAsync(_factory, otherInvoice.Id, 1000m);

        var response = await client.PostAsync(StripeTestHelpers.CheckoutUrl(invoice.Id), null);

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var saved = await StripeTestHelpers.GetAttemptAsync(_factory, otherAttempt.Id);
        Assert.Equal(PaymentAttemptStatus.Pending, saved.Status);
        Assert.DoesNotContain(otherAttempt.ProviderSessionId, _factory.FakeStripe.ExpiredSessionIds);
    }

    [Theory]
    [InlineData(InvoiceStatus.Draft)]
    [InlineData(InvoiceStatus.PendingApproval)]
    [InlineData(InvoiceStatus.Rejected)]
    [InlineData(InvoiceStatus.Paid)]
    [InlineData(InvoiceStatus.Cancelled)]
    public async Task Checkout_for_non_collectable_invoice_returns_400(string status)
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var invoice = await StripeTestHelpers.NewInvoiceAsync(_factory, status);

        var response = await client.PostAsync(StripeTestHelpers.CheckoutUrl(invoice.Id), null);

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.False(string.IsNullOrWhiteSpace(await response.ReadMessageAsync()));
        Assert.Empty(await StripeTestHelpers.GetAttemptsForInvoiceAsync(_factory, invoice.Id));
    }

    [Fact]
    public async Task Checkout_for_excluded_invoice_returns_400()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var invoice = await StripeTestHelpers.NewInvoiceAsync(_factory, InvoiceStatus.Approved, isExcluded: true);

        var response = await client.PostAsync(StripeTestHelpers.CheckoutUrl(invoice.Id), null);

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Empty(await StripeTestHelpers.GetAttemptsForInvoiceAsync(_factory, invoice.Id));
    }

    [Fact]
    public async Task Checkout_with_nothing_left_to_pay_returns_400()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var invoice = await StripeTestHelpers.NewInvoiceAsync(_factory, InvoiceStatus.Approved, subtotal: 1000m);
        await TestData.AddPaymentAsync(_factory, invoice.Id, 1000m);

        var response = await client.PostAsync(StripeTestHelpers.CheckoutUrl(invoice.Id), null);

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Equal("Is invoice ka koi baqaya nahi.", await response.ReadMessageAsync());
        Assert.Empty(await StripeTestHelpers.GetAttemptsForInvoiceAsync(_factory, invoice.Id));
    }

    [Fact]
    public async Task Checkout_for_unsupported_currency_returns_400()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var invoice = await StripeTestHelpers.NewInvoiceAsync(_factory, InvoiceStatus.Approved, currency: "JPY");

        var response = await client.PostAsync(StripeTestHelpers.CheckoutUrl(invoice.Id), null);

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Contains("JPY", await response.ReadMessageAsync());
        Assert.DoesNotContain(_factory.FakeStripe.CreatedSessions, r => r.ClientReferenceId == invoice.Id.ToString());
    }

    [Fact]
    public async Task Checkout_with_unresolved_needs_review_attempt_returns_400()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var invoice = await StripeTestHelpers.NewInvoiceAsync(_factory);
        var review = await TestData.AddPaymentAttemptAsync(
            _factory, invoice.Id, 1000m,
            status: PaymentAttemptStatus.NeedsReview,
            paymentIntentId: StripeTestHelpers.NewPaymentIntentId());

        var response = await client.PostAsync(StripeTestHelpers.CheckoutUrl(invoice.Id), null);

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Equal("Is invoice ki online payment review me hai, pehle usay nipta lein.", await response.ReadMessageAsync());
        Assert.DoesNotContain(_factory.FakeStripe.CreatedSessions, r => r.ClientReferenceId == invoice.Id.ToString());

        var attempt = Assert.Single(await StripeTestHelpers.GetAttemptsForInvoiceAsync(_factory, invoice.Id));
        Assert.Equal(review.Id, attempt.Id);
        Assert.Equal(PaymentAttemptStatus.NeedsReview, attempt.Status);
        Assert.Empty(await StripeTestHelpers.GetAuditAsync(_factory, "PaymentInitiated", invoice.Id.ToString()));
    }

    [Fact]
    public async Task Checkout_with_needs_review_attempt_keeps_open_checkout_untouched()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var invoice = await StripeTestHelpers.NewInvoiceAsync(_factory);
        var open = await TestData.AddPaymentAttemptAsync(_factory, invoice.Id, 1000m);
        await TestData.AddPaymentAttemptAsync(
            _factory, invoice.Id, 1000m,
            status: PaymentAttemptStatus.NeedsReview,
            paymentIntentId: StripeTestHelpers.NewPaymentIntentId());

        var response = await client.PostAsync(StripeTestHelpers.CheckoutUrl(invoice.Id), null);

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.DoesNotContain(open.ProviderSessionId, _factory.FakeStripe.ExpiredSessionIds);
        Assert.Equal(PaymentAttemptStatus.Pending, (await StripeTestHelpers.GetAttemptAsync(_factory, open.Id)).Status);
    }

    [Fact]
    public async Task Checkout_works_again_after_needs_review_attempt_is_dismissed()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var invoice = await StripeTestHelpers.NewInvoiceAsync(_factory);
        var review = await TestData.AddPaymentAttemptAsync(
            _factory, invoice.Id, 1000m,
            status: PaymentAttemptStatus.NeedsReview,
            paymentIntentId: StripeTestHelpers.NewPaymentIntentId());

        var dismiss = await client.PostAsJsonAsync(StripeTestHelpers.ResolveUrl(review.Id),
            new { action = "dismiss", note = "Stripe me khud refund kiya" });
        await dismiss.AssertStatusAsync(HttpStatusCode.OK);

        var response = await client.PostAsync(StripeTestHelpers.CheckoutUrl(invoice.Id), null);

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var attempts = await StripeTestHelpers.GetAttemptsForInvoiceAsync(_factory, invoice.Id);
        Assert.Equal(2, attempts.Count);
        Assert.Equal(PaymentAttemptStatus.Resolved, attempts.Single(a => a.Id == review.Id).Status);
        var current = attempts.Single(a => a.Id != review.Id);
        Assert.Equal(PaymentAttemptStatus.Pending, current.Status);
        Assert.Equal(1000m, current.Amount);
    }

    [Fact]
    public async Task Checkout_for_missing_invoice_returns_400()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);

        var response = await client.PostAsync(StripeTestHelpers.CheckoutUrl(987654321), null);

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Equal("Invoice nahi mila.", await response.ReadMessageAsync());
    }

    [Fact]
    public async Task Employee_cannot_start_checkout()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var invoice = await StripeTestHelpers.NewInvoiceAsync(_factory);

        var response = await client.PostAsync(StripeTestHelpers.CheckoutUrl(invoice.Id), null);

        await response.AssertStatusAsync(HttpStatusCode.Forbidden);
        Assert.Empty(await StripeTestHelpers.GetAttemptsForInvoiceAsync(_factory, invoice.Id));
    }

    [Fact]
    public async Task Anonymous_checkout_returns_401()
    {
        var invoice = await StripeTestHelpers.NewInvoiceAsync(_factory);

        var response = await _factory.CreateClient().PostAsync(StripeTestHelpers.CheckoutUrl(invoice.Id), null);

        await response.AssertStatusAsync(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Stripe_api_error_returns_502_and_creates_no_attempt()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var invoice = await StripeTestHelpers.NewInvoiceAsync(_factory);

        _factory.FakeStripe.ThrowOnCreate = new global::Stripe.StripeException("boom");
        HttpResponseMessage response;
        try
        {
            response = await client.PostAsync(StripeTestHelpers.CheckoutUrl(invoice.Id), null);
        }
        finally
        {
            _factory.FakeStripe.ThrowOnCreate = null;
        }

        await response.AssertStatusAsync(HttpStatusCode.BadGateway);
        Assert.Empty(await StripeTestHelpers.GetAttemptsForInvoiceAsync(_factory, invoice.Id));
        Assert.Empty(await StripeTestHelpers.GetAuditAsync(_factory, "PaymentInitiated", invoice.Id.ToString()));
    }
}
