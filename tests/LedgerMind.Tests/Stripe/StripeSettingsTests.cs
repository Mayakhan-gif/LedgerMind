using System.Net;
using LedgerMind.Models;
using LedgerMind.Tests.Infrastructure;

namespace LedgerMind.Tests.StripePayments;

// Stripe ki keys set nahi
public sealed class StripeNotConfiguredFactory : TestWebApplicationFactory
{
    public StripeNotConfiguredFactory()
    {
        Settings["Stripe:SecretKey"] = "";
        Settings["Stripe:WebhookSecret"] = "";
    }
}

// Bohot chhoti expiry (Stripe 30 minute se kam nahi maanta, hum 31 bhejte hain) aur apne success/cancel address
public sealed class StripeShortExpiryFactory : TestWebApplicationFactory
{
    public StripeShortExpiryFactory()
    {
        Settings["Stripe:CheckoutExpiryMinutes"] = "5";
        Settings["Stripe:SuccessUrl"] = "https://pay.ledgermind.test/success";
        Settings["Stripe:CancelUrl"] = "https://pay.ledgermind.test/cancel";
    }
}

// Bohot lambi expiry (Stripe 24 ghante se zyada nahi maanta, hum 23 ghante 59 minute bhejte hain)
public sealed class StripeLongExpiryFactory : TestWebApplicationFactory
{
    public StripeLongExpiryFactory()
    {
        Settings["Stripe:CheckoutExpiryMinutes"] = "10000";
    }
}

public class StripeNotConfiguredTests : IClassFixture<StripeNotConfiguredFactory>
{
    private readonly StripeNotConfiguredFactory _factory;

    public StripeNotConfiguredTests(StripeNotConfiguredFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Checkout_without_secret_key_returns_500()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var invoice = await StripeTestHelpers.NewInvoiceAsync(_factory);

        var response = await client.PostAsync(StripeTestHelpers.CheckoutUrl(invoice.Id), null);

        await response.AssertStatusAsync(HttpStatusCode.InternalServerError);
        Assert.Equal("Stripe ki setting mukammal nahi.", await response.ReadMessageAsync());
        Assert.Empty(await StripeTestHelpers.GetAttemptsForInvoiceAsync(_factory, invoice.Id));
        Assert.Empty(_factory.FakeStripe.CreatedSessions);
    }

    [Fact]
    public async Task Refund_without_secret_key_returns_500_and_changes_nothing()
    {
        var client = await TestAuth.AdminClientAsync(_factory);
        var invoice = await StripeTestHelpers.NewInvoiceAsync(_factory, InvoiceStatus.Paid);
        var paymentIntentId = StripeTestHelpers.NewPaymentIntentId();
        var attempt = await TestData.AddPaymentAttemptAsync(
            _factory, invoice.Id, 1000m, status: PaymentAttemptStatus.Succeeded, paymentIntentId: paymentIntentId);
        await TestData.AddPaymentAsync(_factory, invoice.Id, 1000m, method: "Stripe", paymentAttemptId: attempt.Id);

        var response = await client.PostAsync(StripeTestHelpers.RefundUrl(attempt.Id), null);

        await response.AssertStatusAsync(HttpStatusCode.InternalServerError);
        Assert.Equal("Stripe ki setting mukammal nahi.", await response.ReadMessageAsync());
        Assert.DoesNotContain(_factory.FakeStripe.Refunds, r => r.PaymentIntentId == paymentIntentId);

        var saved = await StripeTestHelpers.GetAttemptAsync(_factory, attempt.Id);
        Assert.Equal(PaymentAttemptStatus.Succeeded, saved.Status);
        Assert.Equal(0m, saved.RefundedAmount);
        Assert.Single(await StripeTestHelpers.GetPaymentsAsync(_factory, invoice.Id));
    }

    [Fact]
    public async Task Webhook_without_webhook_secret_returns_500()
    {
        var json = StripeWebhookBuilder.CheckoutSessionEvent(
            "checkout.session.completed", "cs_test_not_configured", 100000, "PKR");

        var response = await StripeTestHelpers.SendWebhookAsync(_factory, json);

        await response.AssertStatusAsync(HttpStatusCode.InternalServerError);
        Assert.Equal("Webhook ki setting mukammal nahi.", await response.ReadMessageAsync());
    }
}

public class StripeShortExpiryTests : IClassFixture<StripeShortExpiryFactory>
{
    private readonly StripeShortExpiryFactory _factory;

    public StripeShortExpiryTests(StripeShortExpiryFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Expiry_below_30_minutes_is_raised_to_31_and_configured_urls_are_used()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var invoice = await StripeTestHelpers.NewInvoiceAsync(_factory);
        var before = DateTime.UtcNow;

        var response = await client.PostAsync(StripeTestHelpers.CheckoutUrl(invoice.Id), null);

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var request = Assert.Single(_factory.FakeStripe.CreatedSessions,
            r => r.ClientReferenceId == invoice.Id.ToString());

        // Theek 30 minute Stripe tak pohanch kar kam pad jata hai, is liye ek minute zyada
        Assert.InRange(request.ExpiresAtUtc,
            before.AddMinutes(31).AddSeconds(-1),
            DateTime.UtcNow.AddMinutes(31).AddSeconds(1));
        Assert.Equal("https://pay.ledgermind.test/success", request.SuccessUrl);
        Assert.Equal("https://pay.ledgermind.test/cancel", request.CancelUrl);
    }
}

public class StripeLongExpiryTests : IClassFixture<StripeLongExpiryFactory>
{
    private readonly StripeLongExpiryFactory _factory;

    public StripeLongExpiryTests(StripeLongExpiryFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Expiry_above_24_hours_is_capped_just_under_24_hours()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var invoice = await StripeTestHelpers.NewInvoiceAsync(_factory);
        var before = DateTime.UtcNow;

        var response = await client.PostAsync(StripeTestHelpers.CheckoutUrl(invoice.Id), null);

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var request = Assert.Single(_factory.FakeStripe.CreatedSessions,
            r => r.ClientReferenceId == invoice.Id.ToString());
        Assert.InRange(request.ExpiresAtUtc,
            before.AddMinutes(1439).AddSeconds(-1),
            DateTime.UtcNow.AddMinutes(1439).AddSeconds(1));
    }
}
