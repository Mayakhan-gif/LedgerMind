using Stripe;
using Stripe.Checkout;

namespace LedgerMind.Services;

public class StripeGateway : IStripeGateway
{
    private readonly IConfiguration _config;

    public StripeGateway(IConfiguration config)
    {
        _config = config;
    }

    public async Task<StripeCheckoutSession> CreateCheckoutSessionAsync(
        StripeCheckoutRequest request, CancellationToken cancellationToken = default)
    {
        var metadata = new Dictionary<string, string>(request.Metadata);

        var options = new SessionCreateOptions
        {
            Mode = "payment",
            SuccessUrl = request.SuccessUrl,
            CancelUrl = request.CancelUrl,
            ClientReferenceId = request.ClientReferenceId,
            ExpiresAt = request.ExpiresAtUtc,
            Metadata = metadata,
            PaymentIntentData = new SessionPaymentIntentDataOptions
            {
                Metadata = new Dictionary<string, string>(request.Metadata)
            },
            LineItems = new List<SessionLineItemOptions>
            {
                new SessionLineItemOptions
                {
                    Quantity = 1,
                    PriceData = new SessionLineItemPriceDataOptions
                    {
                        Currency = request.Currency.ToLowerInvariant(),
                        UnitAmount = request.AmountMinor,
                        ProductData = new SessionLineItemPriceDataProductDataOptions
                        {
                            Name = request.ProductName
                        }
                    }
                }
            }
        };

        var service = new SessionService(CreateClient());
        var session = await service.CreateAsync(options, cancellationToken: cancellationToken);
        return new StripeCheckoutSession(session.Id, session.Url);
    }

    public async Task ExpireCheckoutSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var service = new SessionService(CreateClient());
        await service.ExpireAsync(sessionId, cancellationToken: cancellationToken);
    }

    public async Task<StripeRefundResult> RefundPaymentIntentAsync(
        string paymentIntentId,
        long? amountMinor,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        var service = new RefundService(CreateClient());

        // Same key = Stripe wahi pehla refund lauta deta hai (do dafa click par do refund nahi)
        var requestOptions = string.IsNullOrWhiteSpace(idempotencyKey)
            ? null
            : new RequestOptions { IdempotencyKey = idempotencyKey };

        var refund = await service.CreateAsync(
            new RefundCreateOptions
            {
                PaymentIntent = paymentIntentId,
                Amount = amountMinor
            },
            requestOptions,
            cancellationToken);

        return new StripeRefundResult(refund.Id, refund.Status, refund.Amount);
    }

    private StripeClient CreateClient()
    {
        var secretKey = _config["Stripe:SecretKey"];
        if (string.IsNullOrWhiteSpace(secretKey))
            throw new StripeConfigurationException("Stripe:SecretKey set nahi hai.");

        return new StripeClient(secretKey);
    }
}
