using System.Net;
using System.Net.Http.Json;
using LedgerMind.Models;
using LedgerMind.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace LedgerMind.Tests.Invoicing;

// POST /api/Invoices/{id}/cancel: sirf manager, sirf jab paise ka koi record na ho.
// Khule Stripe checkout cancel ke saath band (Expired) ho jate hain.
public class InvoiceCancelTests : IClassFixture<TestWebApplicationFactory>
{
    private const string PaymentsExistMessage = "Is invoice par payments maujood hain, cancel nahi ho sakta.";
    private const string NeedsReviewMessage = "Is invoice ki online payment review me hai, pehle usay nipta lein.";

    private readonly TestWebApplicationFactory _factory;

    public InvoiceCancelTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private async Task<Invoice> AddInvoiceAsync(string status)
    {
        var customer = await TestData.AddCustomerAsync(_factory);
        return await TestData.AddInvoiceAsync(_factory, customer.Id, status: status);
    }

    // Pehle se approve/reject hua invoice (reviewer, waqt aur comment ke saath)
    private async Task<Invoice> AddReviewedInvoiceAsync(
        string status, string reviewerId, DateTime reviewedAtUtc, string comment)
    {
        var invoice = await AddInvoiceAsync(status);
        await _factory.WithDbAsync(async db =>
        {
            var row = await db.Invoices.FirstAsync(i => i.Id == invoice.Id);
            row.ReviewedByUserId = reviewerId;
            row.ReviewedAtUtc = reviewedAtUtc;
            row.ReviewComment = comment;
            await db.SaveChangesAsync();
        });
        return invoice;
    }

    private Task<PaymentAttempt> GetAttemptAsync(int attemptId) =>
        _factory.WithDbAsync(db => db.PaymentAttempts.AsNoTracking().FirstAsync(a => a.Id == attemptId));

    [Fact]
    public async Task Manager_cancels_approved_invoice_without_payments()
    {
        var (manager, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var invoice = await AddInvoiceAsync(InvoiceStatus.Approved);

        var response = await InvoicingApi.PostWithoutBodyAsync(client, $"/api/Invoices/{invoice.Id}/cancel");

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var json = await response.ReadJsonAsync();
        Assert.Equal(InvoiceStatus.Cancelled, json.GetProperty("status").GetString());

        var saved = await InvoicingApi.GetInvoiceAsync(_factory, invoice.Id);
        Assert.Equal(InvoiceStatus.Cancelled, saved.Status);
        Assert.Null(saved.ReviewComment);

        var audit = Assert.Single(await InvoicingApi.AuditRowsAsync(_factory, "InvoiceCancelled", "Invoice", invoice.Id));
        Assert.Equal(manager.Id, audit.UserId);
        Assert.Contains(invoice.InvoiceNumber, audit.Details);
    }

    [Fact]
    public async Task Cancel_without_comment_keeps_reviewer_and_earlier_review_comment()
    {
        var reviewer = await TestAuth.CreateUserAsync(_factory, Roles.FinanceManager);
        var (canceller, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var reviewedAt = new DateTime(2026, 9, 1, 10, 30, 0, DateTimeKind.Utc);
        var invoice = await AddReviewedInvoiceAsync(InvoiceStatus.Rejected, reviewer.Id, reviewedAt, "Tax ghalat hai");

        var response = await InvoicingApi.PostWithoutBodyAsync(client, $"/api/Invoices/{invoice.Id}/cancel");

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var saved = await InvoicingApi.GetInvoiceAsync(_factory, invoice.Id);
        Assert.Equal(InvoiceStatus.Cancelled, saved.Status);
        Assert.Equal(reviewer.Id, saved.ReviewedByUserId);
        Assert.Equal(reviewedAt, saved.ReviewedAtUtc);
        Assert.Equal("Tax ghalat hai", saved.ReviewComment);

        var audit = Assert.Single(await InvoicingApi.AuditRowsAsync(_factory, "InvoiceCancelled", "Invoice", invoice.Id));
        Assert.Equal(canceller.Id, audit.UserId);
    }

    [Fact]
    public async Task Cancel_with_comment_replaces_comment_but_keeps_approver()
    {
        var approver = await TestAuth.CreateUserAsync(_factory, Roles.FinanceManager);
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var reviewedAt = new DateTime(2026, 9, 2, 8, 0, 0, DateTimeKind.Utc);
        var invoice = await AddReviewedInvoiceAsync(InvoiceStatus.Approved, approver.Id, reviewedAt, "Theek hai");

        var response = await client.PostAsJsonAsync($"/api/Invoices/{invoice.Id}/cancel",
            new { comment = "Order band ho gaya" });

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var saved = await InvoicingApi.GetInvoiceAsync(_factory, invoice.Id);
        Assert.Equal(approver.Id, saved.ReviewedByUserId);
        Assert.Equal(reviewedAt, saved.ReviewedAtUtc);
        Assert.Equal("Order band ho gaya", saved.ReviewComment);
    }

    [Fact]
    public async Task Cancel_comment_is_stored_as_review_comment()
    {
        var client = await TestAuth.AdminClientAsync(_factory);
        var invoice = await AddInvoiceAsync(InvoiceStatus.Approved);

        var response = await client.PostAsJsonAsync($"/api/Invoices/{invoice.Id}/cancel",
            new { comment = "Customer ne order wapis le liya" });

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var saved = await InvoicingApi.GetInvoiceAsync(_factory, invoice.Id);
        Assert.Equal(InvoiceStatus.Cancelled, saved.Status);
        Assert.Equal("Customer ne order wapis le liya", saved.ReviewComment);
        var audit = Assert.Single(await InvoicingApi.AuditRowsAsync(_factory, "InvoiceCancelled", "Invoice", invoice.Id));
        Assert.Contains("Customer ne order wapis le liya", audit.Details);
    }

    [Theory]
    [InlineData(InvoiceStatus.Draft)]
    [InlineData(InvoiceStatus.PendingApproval)]
    [InlineData(InvoiceStatus.Rejected)]
    public async Task Cancel_is_allowed_from_other_open_statuses(string status)
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var invoice = await AddInvoiceAsync(status);

        var response = await InvoicingApi.PostWithoutBodyAsync(client, $"/api/Invoices/{invoice.Id}/cancel");

        await response.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Equal(InvoiceStatus.Cancelled, (await InvoicingApi.GetInvoiceAsync(_factory, invoice.Id)).Status);
    }

    [Fact]
    public async Task Cancel_with_a_payment_row_returns_400()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var invoice = await AddInvoiceAsync(InvoiceStatus.Approved);
        await TestData.AddPaymentAsync(_factory, invoice.Id, 100m);

        var response = await InvoicingApi.PostWithoutBodyAsync(client, $"/api/Invoices/{invoice.Id}/cancel");

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Equal(PaymentsExistMessage, await response.ReadMessageAsync());
        Assert.Equal(InvoiceStatus.Approved, (await InvoicingApi.GetInvoiceAsync(_factory, invoice.Id)).Status);
        Assert.Empty(await InvoicingApi.AuditRowsAsync(_factory, "InvoiceCancelled", "Invoice", invoice.Id));
    }

    [Fact]
    public async Task Cancel_with_payment_and_refund_reversal_still_returns_400()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var invoice = await AddInvoiceAsync(InvoiceStatus.Approved);
        await TestData.AddPaymentAsync(_factory, invoice.Id, 100m);
        await TestData.AddPaymentAsync(_factory, invoice.Id, -100m);

        var response = await InvoicingApi.PostWithoutBodyAsync(client, $"/api/Invoices/{invoice.Id}/cancel");

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Equal(PaymentsExistMessage, await response.ReadMessageAsync());
    }

    [Theory]
    [InlineData(InvoiceStatus.Paid)]
    [InlineData(InvoiceStatus.Partial)]
    [InlineData(InvoiceStatus.Cancelled)]
    public async Task Cancel_of_paid_partial_or_cancelled_invoice_returns_400(string status)
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var invoice = await AddInvoiceAsync(status);

        var response = await InvoicingApi.PostWithoutBodyAsync(client, $"/api/Invoices/{invoice.Id}/cancel");

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Equal(status, (await InvoicingApi.GetInvoiceAsync(_factory, invoice.Id)).Status);
    }

    [Fact]
    public async Task Cancel_expires_open_stripe_checkout_and_cancels_invoice()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var invoice = await AddInvoiceAsync(InvoiceStatus.Approved);
        var attempt = await TestData.AddPaymentAttemptAsync(
            _factory, invoice.Id, invoice.Total, status: PaymentAttemptStatus.Pending);

        var response = await InvoicingApi.PostWithoutBodyAsync(client, $"/api/Invoices/{invoice.Id}/cancel");

        await response.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Equal(InvoiceStatus.Cancelled, (await InvoicingApi.GetInvoiceAsync(_factory, invoice.Id)).Status);
        Assert.Contains(attempt.ProviderSessionId, _factory.FakeStripe.ExpiredSessionIds);

        var saved = await GetAttemptAsync(attempt.Id);
        Assert.Equal(PaymentAttemptStatus.Expired, saved.Status);
        Assert.NotNull(saved.CompletedAtUtc);
        Assert.False(string.IsNullOrEmpty(saved.FailureReason));

        var audit = Assert.Single(await InvoicingApi.AuditRowsAsync(_factory, "InvoiceCancelled", "Invoice", invoice.Id));
        Assert.Contains("Stripe checkout band", audit.Details);
    }

    [Fact]
    public async Task Cancel_still_succeeds_when_stripe_session_is_already_closed()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var invoice = await AddInvoiceAsync(InvoiceStatus.Approved);
        var attempt = await TestData.AddPaymentAttemptAsync(
            _factory, invoice.Id, invoice.Total, status: PaymentAttemptStatus.Pending);

        _factory.FakeStripe.ThrowOnExpire = new global::Stripe.StripeException("session already expired");
        HttpResponseMessage response;
        try
        {
            response = await InvoicingApi.PostWithoutBodyAsync(client, $"/api/Invoices/{invoice.Id}/cancel");
        }
        finally
        {
            _factory.FakeStripe.ThrowOnExpire = null;
        }

        await response.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Equal(InvoiceStatus.Cancelled, (await InvoicingApi.GetInvoiceAsync(_factory, invoice.Id)).Status);
        Assert.Equal(PaymentAttemptStatus.Expired, (await GetAttemptAsync(attempt.Id)).Status);
    }

    // Checkout bane, phir Stripe:SecretKey hata di gayi: gateway InvalidOperationException deta hai, cancel phir bhi ho
    [Fact]
    public async Task Cancel_still_succeeds_when_stripe_is_no_longer_configured()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var invoice = await AddInvoiceAsync(InvoiceStatus.Approved);
        var attempt = await TestData.AddPaymentAttemptAsync(
            _factory, invoice.Id, invoice.Total, status: PaymentAttemptStatus.Pending);

        _factory.FakeStripe.ThrowOnExpire = new InvalidOperationException("Stripe:SecretKey set nahi hai.");
        HttpResponseMessage response;
        try
        {
            response = await InvoicingApi.PostWithoutBodyAsync(client, $"/api/Invoices/{invoice.Id}/cancel");
        }
        finally
        {
            _factory.FakeStripe.ThrowOnExpire = null;
        }

        await response.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Equal(InvoiceStatus.Cancelled, (await InvoicingApi.GetInvoiceAsync(_factory, invoice.Id)).Status);
        Assert.Equal(PaymentAttemptStatus.Expired, (await GetAttemptAsync(attempt.Id)).Status);
        Assert.Single(await InvoicingApi.AuditRowsAsync(_factory, "InvoiceCancelled", "Invoice", invoice.Id));
    }

    [Fact]
    public async Task Cancel_saves_nothing_when_stripe_cannot_be_reached()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var invoice = await AddInvoiceAsync(InvoiceStatus.Approved);
        var attempt = await TestData.AddPaymentAttemptAsync(
            _factory, invoice.Id, invoice.Total, status: PaymentAttemptStatus.Pending);

        _factory.FakeStripe.ThrowOnExpire = new HttpRequestException("network down");
        HttpResponseMessage response;
        try
        {
            response = await InvoicingApi.PostWithoutBodyAsync(client, $"/api/Invoices/{invoice.Id}/cancel");
        }
        finally
        {
            _factory.FakeStripe.ThrowOnExpire = null;
        }

        await response.AssertStatusAsync(HttpStatusCode.InternalServerError);
        Assert.Equal(InvoiceStatus.Approved, (await InvoicingApi.GetInvoiceAsync(_factory, invoice.Id)).Status);
        Assert.Equal(PaymentAttemptStatus.Pending, (await GetAttemptAsync(attempt.Id)).Status);
        Assert.Empty(await InvoicingApi.AuditRowsAsync(_factory, "InvoiceCancelled", "Invoice", invoice.Id));
    }

    [Fact]
    public async Task Cancel_with_unresolved_online_payment_returns_400_and_leaves_checkout_open()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var invoice = await AddInvoiceAsync(InvoiceStatus.Approved);
        await TestData.AddPaymentAttemptAsync(_factory, invoice.Id, invoice.Total, status: PaymentAttemptStatus.NeedsReview);
        var open = await TestData.AddPaymentAttemptAsync(
            _factory, invoice.Id, invoice.Total, status: PaymentAttemptStatus.Pending);

        var response = await InvoicingApi.PostWithoutBodyAsync(client, $"/api/Invoices/{invoice.Id}/cancel");

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Equal(NeedsReviewMessage, await response.ReadMessageAsync());
        Assert.Equal(InvoiceStatus.Approved, (await InvoicingApi.GetInvoiceAsync(_factory, invoice.Id)).Status);
        Assert.Equal(PaymentAttemptStatus.Pending, (await GetAttemptAsync(open.Id)).Status);
        Assert.DoesNotContain(open.ProviderSessionId, _factory.FakeStripe.ExpiredSessionIds);
    }

    [Fact]
    public async Task Cancel_is_allowed_when_only_closed_checkouts_exist_and_does_not_call_stripe()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var invoice = await AddInvoiceAsync(InvoiceStatus.Approved);
        var expired = await TestData.AddPaymentAttemptAsync(
            _factory, invoice.Id, invoice.Total, status: PaymentAttemptStatus.Expired);
        var failed = await TestData.AddPaymentAttemptAsync(
            _factory, invoice.Id, invoice.Total, status: PaymentAttemptStatus.Failed);
        var resolved = await TestData.AddPaymentAttemptAsync(
            _factory, invoice.Id, invoice.Total, status: PaymentAttemptStatus.Resolved);

        var response = await InvoicingApi.PostWithoutBodyAsync(client, $"/api/Invoices/{invoice.Id}/cancel");

        await response.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Equal(InvoiceStatus.Cancelled, (await InvoicingApi.GetInvoiceAsync(_factory, invoice.Id)).Status);
        Assert.DoesNotContain(expired.ProviderSessionId, _factory.FakeStripe.ExpiredSessionIds);
        Assert.DoesNotContain(failed.ProviderSessionId, _factory.FakeStripe.ExpiredSessionIds);
        Assert.DoesNotContain(resolved.ProviderSessionId, _factory.FakeStripe.ExpiredSessionIds);
        Assert.Equal(PaymentAttemptStatus.Resolved, (await GetAttemptAsync(resolved.Id)).Status);
    }

    [Fact]
    public async Task Employee_cannot_cancel()
    {
        var (employee, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var customer = await TestData.AddCustomerAsync(_factory);
        var invoice = await TestData.AddInvoiceAsync(_factory, customer.Id, createdByUserId: employee.Id);

        var response = await InvoicingApi.PostWithoutBodyAsync(client, $"/api/Invoices/{invoice.Id}/cancel");

        await response.AssertStatusAsync(HttpStatusCode.Forbidden);
        Assert.Equal(InvoiceStatus.Draft, (await InvoicingApi.GetInvoiceAsync(_factory, invoice.Id)).Status);
    }

    [Fact]
    public async Task Cancel_of_unknown_invoice_returns_404()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);

        var response = await InvoicingApi.PostWithoutBodyAsync(client, $"/api/Invoices/{int.MaxValue}/cancel");

        await response.AssertStatusAsync(HttpStatusCode.NotFound);
    }
}
