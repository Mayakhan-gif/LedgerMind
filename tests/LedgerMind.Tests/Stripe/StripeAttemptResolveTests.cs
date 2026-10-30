using System.Net;
using System.Net.Http.Json;
using LedgerMind.Models;
using LedgerMind.Tests.Infrastructure;

namespace LedgerMind.Tests.StripePayments;

public class StripeAttemptResolveTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public StripeAttemptResolveTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private Task<PaymentAttempt> NeedsReviewAttemptAsync(Invoice invoice, decimal amount, string currency = "PKR") =>
        TestData.AddPaymentAttemptAsync(
            _factory, invoice.Id, amount, currency,
            status: PaymentAttemptStatus.NeedsReview,
            paymentIntentId: StripeTestHelpers.NewPaymentIntentId());

    // ---------- GET /api/Stripe/attempts ----------

    [Fact]
    public async Task List_filters_by_status_and_invoice()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var invoice = await StripeTestHelpers.NewInvoiceAsync(_factory);
        var review = await NeedsReviewAttemptAsync(invoice, 1000m);
        var pending = await TestData.AddPaymentAttemptAsync(_factory, invoice.Id, 1000m);

        var response = await client.GetAsync($"/api/Stripe/attempts?status=NeedsReview&invoiceId={invoice.Id}");

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var item = Assert.Single((await response.ReadJsonAsync()).EnumerateArray());
        Assert.Equal(review.Id, item.GetProperty("id").GetInt32());
        Assert.Equal(invoice.Id, item.GetProperty("invoiceId").GetInt32());
        Assert.Equal(invoice.InvoiceNumber, item.GetProperty("invoiceNumber").GetString());
        Assert.Equal(1000m, item.GetProperty("amount").GetDecimal());
        Assert.Equal("PKR", item.GetProperty("currency").GetString());
        Assert.Equal(PaymentAttemptStatus.NeedsReview, item.GetProperty("status").GetString());
        Assert.Equal(0m, item.GetProperty("refundedAmount").GetDecimal());
        Assert.Equal(review.ProviderSessionId, item.GetProperty("providerSessionId").GetString());
        Assert.Equal(review.ProviderPaymentIntentId, item.GetProperty("providerPaymentIntentId").GetString());
        Assert.True(item.TryGetProperty("createdAtUtc", out _));
        Assert.True(item.TryGetProperty("completedAtUtc", out _));
        Assert.True(item.TryGetProperty("resolvedAtUtc", out _));
        Assert.True(item.TryGetProperty("resolutionNote", out _));
        Assert.True(item.TryGetProperty("failureReason", out _));
        Assert.NotEqual(pending.Id, item.GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task List_by_invoice_returns_newest_first()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Admin);
        var invoice = await StripeTestHelpers.NewInvoiceAsync(_factory);
        var first = await TestData.AddPaymentAttemptAsync(_factory, invoice.Id, 1000m, status: PaymentAttemptStatus.Expired);
        var second = await TestData.AddPaymentAttemptAsync(_factory, invoice.Id, 1000m);

        var response = await client.GetAsync($"/api/Stripe/attempts?invoiceId={invoice.Id}");

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var ids = (await response.ReadJsonAsync()).EnumerateArray()
            .Select(e => e.GetProperty("id").GetInt32())
            .ToList();
        Assert.Equal(new[] { second.Id, first.Id }, ids);
    }

    [Fact]
    public async Task List_status_filter_is_case_insensitive()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var invoice = await StripeTestHelpers.NewInvoiceAsync(_factory);
        var review = await NeedsReviewAttemptAsync(invoice, 1000m);

        var response = await client.GetAsync($"/api/Stripe/attempts?status=needsreview&invoiceId={invoice.Id}");

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var item = Assert.Single((await response.ReadJsonAsync()).EnumerateArray());
        Assert.Equal(review.Id, item.GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task List_with_unknown_status_returns_400()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);

        var response = await client.GetAsync("/api/Stripe/attempts?status=Whatever");

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Employee_cannot_list_attempts()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);

        var response = await client.GetAsync("/api/Stripe/attempts");

        await response.AssertStatusAsync(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Anonymous_cannot_list_attempts()
    {
        var response = await _factory.CreateClient().GetAsync("/api/Stripe/attempts");

        await response.AssertStatusAsync(HttpStatusCode.Unauthorized);
    }

    // ---------- POST /api/Stripe/attempts/{id}/resolve ----------

    [Fact]
    public async Task Resolve_apply_records_payment_and_marks_attempt_succeeded()
    {
        var (manager, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var invoice = await StripeTestHelpers.NewInvoiceAsync(_factory, subtotal: 1000m);
        var attempt = await NeedsReviewAttemptAsync(invoice, 1000m);

        var response = await client.PostAsJsonAsync(StripeTestHelpers.ResolveUrl(attempt.Id),
            new { action = "apply", note = "Stripe dashboard me check kiya" });

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var json = await response.ReadJsonAsync();
        Assert.Equal(attempt.Id, json.GetProperty("id").GetInt32());
        Assert.Equal(PaymentAttemptStatus.Succeeded, json.GetProperty("status").GetString());
        Assert.Equal("Stripe dashboard me check kiya", json.GetProperty("resolutionNote").GetString());
        Assert.Equal(invoice.InvoiceNumber, json.GetProperty("invoiceNumber").GetString());

        var payment = Assert.Single(await StripeTestHelpers.GetPaymentsAsync(_factory, invoice.Id));
        Assert.Equal(1000m, payment.Amount);
        Assert.Equal(attempt.Id, payment.PaymentAttemptId);
        Assert.Equal("Stripe", payment.Method);
        Assert.Equal(manager.Id, payment.CreatedByUserId);
        Assert.StartsWith("Resolved:", payment.Note);

        Assert.Equal(InvoiceStatus.Paid, (await StripeTestHelpers.GetInvoiceAsync(_factory, invoice.Id)).Status);

        var saved = await StripeTestHelpers.GetAttemptAsync(_factory, attempt.Id);
        Assert.Equal(PaymentAttemptStatus.Succeeded, saved.Status);
        Assert.Equal(manager.Id, saved.ResolvedByUserId);
        Assert.NotNull(saved.ResolvedAtUtc);
        Assert.Equal("Stripe dashboard me check kiya", saved.ResolutionNote);

        var audit = Assert.Single(await StripeTestHelpers.GetAuditAsync(_factory, "PaymentAttemptResolved", attempt.Id.ToString()));
        Assert.Equal(manager.Id, audit.UserId);
    }

    // Rs 1000 ka attempt, Stripe ne 1200 liye (NeedsReview), zaid 200 Stripe dashboard se wapis hue
    private async Task<(Invoice Invoice, PaymentAttempt Attempt, string PaymentIntentId)> OverchargedAndExtraRefundedAsync()
    {
        var (invoice, attempt, paymentIntentId) = await StripeTestHelpers.CreateNeedsReviewAttemptAsync(
            _factory, invoiceTotal: 1000m, chargedMinor: 120000);

        var extraRefund = await StripeTestHelpers.SendWebhookAsync(
            _factory, StripeWebhookBuilder.ChargeRefundedEvent(paymentIntentId, 120000, 20000, "PKR"));
        await extraRefund.AssertStatusAsync(HttpStatusCode.OK);

        var saved = await StripeTestHelpers.GetAttemptAsync(_factory, attempt.Id);
        Assert.Equal(PaymentAttemptStatus.NeedsReview, saved.Status);
        Assert.Equal(200m, saved.RefundedAmount);
        Assert.Empty(await StripeTestHelpers.GetPaymentsAsync(_factory, invoice.Id));
        return (invoice, saved, paymentIntentId);
    }

    [Fact]
    public async Task Resolve_apply_after_extra_charge_was_refunded_records_attempt_amount()
    {
        var (manager, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var (invoice, attempt, _) = await OverchargedAndExtraRefundedAsync();

        var response = await client.PostAsJsonAsync(StripeTestHelpers.ResolveUrl(attempt.Id), new { action = "apply" });

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var json = await response.ReadJsonAsync();
        Assert.Equal(PaymentAttemptStatus.Succeeded, json.GetProperty("status").GetString());
        Assert.Equal(200m, json.GetProperty("refundedAmount").GetDecimal());

        // Customer ki asal payment 1200 - 200 = 1000, yani attempt ki raqam
        var payment = Assert.Single(await StripeTestHelpers.GetPaymentsAsync(_factory, invoice.Id));
        Assert.Equal(1000m, payment.Amount);
        Assert.Equal(attempt.Id, payment.PaymentAttemptId);
        Assert.Equal(manager.Id, payment.CreatedByUserId);
        Assert.Equal(InvoiceStatus.Paid, (await StripeTestHelpers.GetInvoiceAsync(_factory, invoice.Id)).Status);

        var saved = await StripeTestHelpers.GetAttemptAsync(_factory, attempt.Id);
        Assert.Equal(PaymentAttemptStatus.Succeeded, saved.Status);
        Assert.Equal(200m, saved.RefundedAmount);
        Assert.Equal(manager.Id, saved.ResolvedByUserId);
    }

    [Fact]
    public async Task Full_refund_after_resolve_apply_reverses_exactly_the_recorded_amount()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var (invoice, attempt, paymentIntentId) = await OverchargedAndExtraRefundedAsync();
        var apply = await client.PostAsJsonAsync(StripeTestHelpers.ResolveUrl(attempt.Id), new { action = "apply" });
        await apply.AssertStatusAsync(HttpStatusCode.OK);

        // Baqi 1000 bhi wapis: Stripe ka kul amount_refunded ab 1200
        var fullRefund = await StripeTestHelpers.SendWebhookAsync(
            _factory, StripeWebhookBuilder.ChargeRefundedEvent(paymentIntentId, 120000, 120000, "PKR"));

        await fullRefund.AssertStatusAsync(HttpStatusCode.OK);
        var amounts = (await StripeTestHelpers.GetPaymentsAsync(_factory, invoice.Id)).Select(p => p.Amount).ToList();
        Assert.Equal(new[] { 1000m, -1000m }, amounts);
        Assert.Equal(InvoiceStatus.Approved, (await StripeTestHelpers.GetInvoiceAsync(_factory, invoice.Id)).Status);

        var saved = await StripeTestHelpers.GetAttemptAsync(_factory, attempt.Id);
        Assert.Equal(PaymentAttemptStatus.Refunded, saved.Status);
        Assert.Equal(1200m, saved.RefundedAmount);
    }

    [Fact]
    public async Task Resolve_dismiss_after_partial_refund_records_nothing()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var (invoice, attempt, _) = await OverchargedAndExtraRefundedAsync();

        var response = await client.PostAsJsonAsync(StripeTestHelpers.ResolveUrl(attempt.Id), new { action = "dismiss" });

        await response.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Empty(await StripeTestHelpers.GetPaymentsAsync(_factory, invoice.Id));
        Assert.Equal(InvoiceStatus.Approved, (await StripeTestHelpers.GetInvoiceAsync(_factory, invoice.Id)).Status);
        Assert.Equal(PaymentAttemptStatus.Resolved, (await StripeTestHelpers.GetAttemptAsync(_factory, attempt.Id)).Status);
    }

    [Fact]
    public async Task Resolve_after_full_refund_webhook_returns_400()
    {
        // Refund webhook ne NeedsReview attempt ko Refunded kar diya: ab apply se paisa darj nahi hona chahiye
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var (invoice, attempt, paymentIntentId) = await StripeTestHelpers.CreateNeedsReviewAttemptAsync(
            _factory, invoiceTotal: 1000m, chargedMinor: 120000);
        var refund = await StripeTestHelpers.SendWebhookAsync(
            _factory, StripeWebhookBuilder.ChargeRefundedEvent(paymentIntentId, 120000, 120000, "PKR"));
        await refund.AssertStatusAsync(HttpStatusCode.OK);

        var response = await client.PostAsJsonAsync(StripeTestHelpers.ResolveUrl(attempt.Id), new { action = "apply" });

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Empty(await StripeTestHelpers.GetPaymentsAsync(_factory, invoice.Id));
        Assert.Equal(PaymentAttemptStatus.Refunded, (await StripeTestHelpers.GetAttemptAsync(_factory, attempt.Id)).Status);
    }

    [Fact]
    public async Task Resolve_action_is_case_insensitive()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Admin);
        var invoice = await StripeTestHelpers.NewInvoiceAsync(_factory, subtotal: 1000m);
        var attempt = await NeedsReviewAttemptAsync(invoice, 400m);

        var response = await client.PostAsJsonAsync(StripeTestHelpers.ResolveUrl(attempt.Id), new { action = " APPLY " });

        await response.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Equal(400m, Assert.Single(await StripeTestHelpers.GetPaymentsAsync(_factory, invoice.Id)).Amount);
        Assert.Equal(InvoiceStatus.Partial, (await StripeTestHelpers.GetInvoiceAsync(_factory, invoice.Id)).Status);
    }

    [Fact]
    public async Task Resolve_apply_more_than_balance_returns_400()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var invoice = await StripeTestHelpers.NewInvoiceAsync(_factory, InvoiceStatus.Partial, subtotal: 1000m);
        await TestData.AddPaymentAsync(_factory, invoice.Id, 500m);
        var attempt = await NeedsReviewAttemptAsync(invoice, 1000m);

        var response = await client.PostAsJsonAsync(StripeTestHelpers.ResolveUrl(attempt.Id), new { action = "apply" });

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Contains("baqaya", await response.ReadMessageAsync());
        Assert.Single(await StripeTestHelpers.GetPaymentsAsync(_factory, invoice.Id));

        var saved = await StripeTestHelpers.GetAttemptAsync(_factory, attempt.Id);
        Assert.Equal(PaymentAttemptStatus.NeedsReview, saved.Status);
        Assert.Null(saved.ResolvedAtUtc);
    }

    [Fact]
    public async Task Resolve_apply_on_paid_invoice_returns_400()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var invoice = await StripeTestHelpers.NewInvoiceAsync(_factory, InvoiceStatus.Paid, subtotal: 1000m);
        var attempt = await NeedsReviewAttemptAsync(invoice, 1000m);

        var response = await client.PostAsJsonAsync(StripeTestHelpers.ResolveUrl(attempt.Id), new { action = "apply" });

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Empty(await StripeTestHelpers.GetPaymentsAsync(_factory, invoice.Id));
        Assert.Equal(PaymentAttemptStatus.NeedsReview, (await StripeTestHelpers.GetAttemptAsync(_factory, attempt.Id)).Status);
    }

    [Fact]
    public async Task Resolve_apply_with_different_currency_returns_400()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var invoice = await StripeTestHelpers.NewInvoiceAsync(_factory, subtotal: 1000m, currency: "PKR");
        var attempt = await NeedsReviewAttemptAsync(invoice, 1000m, currency: "USD");

        var response = await client.PostAsJsonAsync(StripeTestHelpers.ResolveUrl(attempt.Id), new { action = "apply" });

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Empty(await StripeTestHelpers.GetPaymentsAsync(_factory, invoice.Id));
    }

    [Fact]
    public async Task Resolve_dismiss_closes_attempt_without_payment()
    {
        var (manager, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var invoice = await StripeTestHelpers.NewInvoiceAsync(_factory);
        var attempt = await NeedsReviewAttemptAsync(invoice, 1000m);

        var response = await client.PostAsJsonAsync(StripeTestHelpers.ResolveUrl(attempt.Id),
            new { action = "dismiss", note = "Stripe me khud refund kiya" });

        await response.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Equal(PaymentAttemptStatus.Resolved, (await response.ReadJsonAsync()).GetProperty("status").GetString());
        Assert.Empty(await StripeTestHelpers.GetPaymentsAsync(_factory, invoice.Id));
        Assert.Equal(InvoiceStatus.Approved, (await StripeTestHelpers.GetInvoiceAsync(_factory, invoice.Id)).Status);

        var saved = await StripeTestHelpers.GetAttemptAsync(_factory, attempt.Id);
        Assert.Equal(PaymentAttemptStatus.Resolved, saved.Status);
        Assert.Equal(manager.Id, saved.ResolvedByUserId);
        Assert.NotNull(saved.ResolvedAtUtc);
        Assert.Equal("Stripe me khud refund kiya", saved.ResolutionNote);
        Assert.Single(await StripeTestHelpers.GetAuditAsync(_factory, "PaymentAttemptResolved", attempt.Id.ToString()));
    }

    [Fact]
    public async Task Resolving_an_already_resolved_attempt_returns_400()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var invoice = await StripeTestHelpers.NewInvoiceAsync(_factory);
        var attempt = await NeedsReviewAttemptAsync(invoice, 1000m);

        var first = await client.PostAsJsonAsync(StripeTestHelpers.ResolveUrl(attempt.Id), new { action = "dismiss" });
        await first.AssertStatusAsync(HttpStatusCode.OK);
        var second = await client.PostAsJsonAsync(StripeTestHelpers.ResolveUrl(attempt.Id), new { action = "apply" });

        await second.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Empty(await StripeTestHelpers.GetPaymentsAsync(_factory, invoice.Id));
    }

    [Theory]
    [InlineData(PaymentAttemptStatus.Pending)]
    [InlineData(PaymentAttemptStatus.Succeeded)]
    [InlineData(PaymentAttemptStatus.Expired)]
    [InlineData(PaymentAttemptStatus.Refunded)]
    public async Task Resolve_attempt_not_in_needs_review_returns_400(string status)
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var invoice = await StripeTestHelpers.NewInvoiceAsync(_factory);
        var attempt = await TestData.AddPaymentAttemptAsync(_factory, invoice.Id, 1000m, status: status);

        var response = await client.PostAsJsonAsync(StripeTestHelpers.ResolveUrl(attempt.Id), new { action = "apply" });

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Empty(await StripeTestHelpers.GetPaymentsAsync(_factory, invoice.Id));
        Assert.Equal(status, (await StripeTestHelpers.GetAttemptAsync(_factory, attempt.Id)).Status);
    }

    [Fact]
    public async Task Resolve_with_invalid_action_returns_400()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var invoice = await StripeTestHelpers.NewInvoiceAsync(_factory);
        var attempt = await NeedsReviewAttemptAsync(invoice, 1000m);

        var response = await client.PostAsJsonAsync(StripeTestHelpers.ResolveUrl(attempt.Id), new { action = "approve" });

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Equal("Action 'apply' ya 'dismiss' honi chahiye.", await response.ReadMessageAsync());
        Assert.Equal(PaymentAttemptStatus.NeedsReview, (await StripeTestHelpers.GetAttemptAsync(_factory, attempt.Id)).Status);
    }

    [Fact]
    public async Task Resolve_without_action_returns_400()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var invoice = await StripeTestHelpers.NewInvoiceAsync(_factory);
        var attempt = await NeedsReviewAttemptAsync(invoice, 1000m);

        var response = await client.PostAsJsonAsync(StripeTestHelpers.ResolveUrl(attempt.Id), new { note = "kuch nahi" });

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Equal(PaymentAttemptStatus.NeedsReview, (await StripeTestHelpers.GetAttemptAsync(_factory, attempt.Id)).Status);
    }

    [Fact]
    public async Task Resolve_with_too_long_note_returns_400()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var invoice = await StripeTestHelpers.NewInvoiceAsync(_factory);
        var attempt = await NeedsReviewAttemptAsync(invoice, 1000m);

        var response = await client.PostAsJsonAsync(StripeTestHelpers.ResolveUrl(attempt.Id),
            new { action = "dismiss", note = new string('x', 501) });

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Equal(PaymentAttemptStatus.NeedsReview, (await StripeTestHelpers.GetAttemptAsync(_factory, attempt.Id)).Status);
    }

    [Fact]
    public async Task Resolve_unknown_attempt_returns_404()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);

        var response = await client.PostAsJsonAsync(StripeTestHelpers.ResolveUrl(987654321), new { action = "dismiss" });

        await response.AssertStatusAsync(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Employee_cannot_resolve_attempt()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var invoice = await StripeTestHelpers.NewInvoiceAsync(_factory);
        var attempt = await NeedsReviewAttemptAsync(invoice, 1000m);

        var response = await client.PostAsJsonAsync(StripeTestHelpers.ResolveUrl(attempt.Id), new { action = "dismiss" });

        await response.AssertStatusAsync(HttpStatusCode.Forbidden);
        Assert.Equal(PaymentAttemptStatus.NeedsReview, (await StripeTestHelpers.GetAttemptAsync(_factory, attempt.Id)).Status);
    }
}
