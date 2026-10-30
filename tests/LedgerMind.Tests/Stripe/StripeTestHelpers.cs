using System.Net;
using LedgerMind.Models;
using LedgerMind.Services;
using LedgerMind.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

// Namespace "...Stripe" nahi rakha: warna LedgerMind.Tests ke andar har jagah
// "Stripe.StripeException" jaisa naam asal Stripe namespace ki jagah is namespace par jata.
namespace LedgerMind.Tests.StripePayments;

internal static class StripeTestHelpers
{
    public const string WebhookUrl = "/api/Stripe/webhook";

    public static string CheckoutUrl(int invoiceId) => $"/api/Stripe/checkout/{invoiceId}";
    public static string ResolveUrl(int attemptId) => $"/api/Stripe/attempts/{attemptId}/resolve";
    public static string RefundUrl(int attemptId) => $"/api/Stripe/attempts/{attemptId}/refund";

    public static string NewPaymentIntentId() => $"pi_test_{Guid.NewGuid():N}";

    // Naya customer + invoice (default: Approved, 1000 PKR)
    public static async Task<Invoice> NewInvoiceAsync(
        TestWebApplicationFactory factory,
        string status = InvoiceStatus.Approved,
        decimal subtotal = 1000m,
        string currency = "PKR",
        bool isExcluded = false)
    {
        var customer = await TestData.AddCustomerAsync(factory);
        return await TestData.AddInvoiceAsync(
            factory, customer.Id, status: status, subtotal: subtotal, currency: currency, isExcluded: isExcluded);
    }

    // Sahi signature ke saath webhook (login ke baghair, jaise Stripe bhejta hai)
    public static Task<HttpResponseMessage> SendWebhookAsync(TestWebApplicationFactory factory, string json)
    {
        var client = factory.CreateClient();
        return client.SendAsync(StripeWebhookBuilder.SignedRequest(json));
    }

    public static Task<PaymentAttempt> GetAttemptAsync(TestWebApplicationFactory factory, int attemptId) =>
        factory.WithDbAsync(db => db.PaymentAttempts.AsNoTracking().SingleAsync(a => a.Id == attemptId));

    public static Task<List<PaymentAttempt>> GetAttemptsForInvoiceAsync(TestWebApplicationFactory factory, int invoiceId) =>
        factory.WithDbAsync(db => db.PaymentAttempts
            .AsNoTracking()
            .Where(a => a.InvoiceId == invoiceId)
            .OrderBy(a => a.Id)
            .ToListAsync());

    public static Task<Invoice> GetInvoiceAsync(TestWebApplicationFactory factory, int invoiceId) =>
        factory.WithDbAsync(db => db.Invoices.AsNoTracking().SingleAsync(i => i.Id == invoiceId));

    public static Task<List<Payment>> GetPaymentsAsync(TestWebApplicationFactory factory, int invoiceId) =>
        factory.WithDbAsync(db => db.Payments
            .AsNoTracking()
            .Where(p => p.InvoiceId == invoiceId)
            .OrderBy(p => p.Id)
            .ToListAsync());

    public static Task<List<AuditLog>> GetAuditAsync(TestWebApplicationFactory factory, string action, string entityId) =>
        factory.WithDbAsync(db => db.AuditLogs
            .AsNoTracking()
            .Where(a => a.Action == action && a.EntityId == entityId)
            .ToListAsync());

    public static Task<PaymentWebhookEvent?> GetWebhookEventAsync(TestWebApplicationFactory factory, string eventId) =>
        factory.WithDbAsync(db => db.PaymentWebhookEvents
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.EventId == eventId));

    public static Task SetInvoiceStatusAsync(TestWebApplicationFactory factory, int invoiceId, string status) =>
        factory.WithDbAsync(async db =>
        {
            var invoice = await db.Invoices.SingleAsync(i => i.Id == invoiceId);
            invoice.Status = status;
            await db.SaveChangesAsync();
        });

    // Invoice (total) + pehle se jama raqam + baqaya ka Pending attempt, phir kamyab webhook.
    // Nateeja: Succeeded attempt aur uski ledger entry.
    public static async Task<(Invoice Invoice, PaymentAttempt Attempt, string PaymentIntentId)> CreateSucceededAttemptAsync(
        TestWebApplicationFactory factory,
        decimal invoiceTotal = 1000m,
        decimal alreadyPaid = 0m,
        string currency = "PKR")
    {
        var invoice = await NewInvoiceAsync(
            factory,
            alreadyPaid > 0 ? InvoiceStatus.Partial : InvoiceStatus.Approved,
            invoiceTotal,
            currency);

        if (alreadyPaid > 0)
            await TestData.AddPaymentAsync(factory, invoice.Id, alreadyPaid);

        var amount = invoiceTotal - alreadyPaid;
        var attempt = await TestData.AddPaymentAttemptAsync(factory, invoice.Id, amount, currency);
        var paymentIntentId = NewPaymentIntentId();

        var json = StripeWebhookBuilder.CheckoutSessionEvent(
            "checkout.session.completed",
            attempt.ProviderSessionId,
            InvoiceRules.ToMinorUnits(amount),
            currency,
            paymentIntentId: paymentIntentId);

        var response = await SendWebhookAsync(factory, json);
        await response.AssertStatusAsync(HttpStatusCode.OK);

        var saved = await GetAttemptAsync(factory, attempt.Id);
        Assert.Equal(PaymentAttemptStatus.Succeeded, saved.Status);
        return (invoice, saved, paymentIntentId);
    }

    // Stripe ne paisa liya magar amount match nahi hua: NeedsReview attempt (payment intent ke saath)
    public static async Task<(Invoice Invoice, PaymentAttempt Attempt, string PaymentIntentId)> CreateNeedsReviewAttemptAsync(
        TestWebApplicationFactory factory,
        decimal invoiceTotal = 1000m,
        long chargedMinor = 120000)
    {
        var invoice = await NewInvoiceAsync(factory, InvoiceStatus.Approved, invoiceTotal);
        var attempt = await TestData.AddPaymentAttemptAsync(factory, invoice.Id, invoiceTotal);
        var paymentIntentId = NewPaymentIntentId();

        var json = StripeWebhookBuilder.CheckoutSessionEvent(
            "checkout.session.completed",
            attempt.ProviderSessionId,
            chargedMinor,
            "PKR",
            paymentIntentId: paymentIntentId);

        var response = await SendWebhookAsync(factory, json);
        await response.AssertStatusAsync(HttpStatusCode.OK);

        var saved = await GetAttemptAsync(factory, attempt.Id);
        Assert.Equal(PaymentAttemptStatus.NeedsReview, saved.Status);
        return (invoice, saved, paymentIntentId);
    }
}
