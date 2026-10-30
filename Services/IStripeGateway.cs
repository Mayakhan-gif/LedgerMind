namespace LedgerMind.Services;

// Stripe API ke saath baat ek interface ke peeche, taake tests asli Stripe ke baghair chal saken
public interface IStripeGateway
{
    Task<StripeCheckoutSession> CreateCheckoutSessionAsync(
        StripeCheckoutRequest request, CancellationToken cancellationToken = default);

    // Purana khula checkout band karna (us par ab payment na ho sake)
    Task ExpireCheckoutSessionAsync(string sessionId, CancellationToken cancellationToken = default);

    // amountMinor null = jitna baqi hai sab wapis. idempotencyKey: dobara bheji gayi request par
    // Stripe naya refund nahi banata, pehla wala lauta deta hai.
    Task<StripeRefundResult> RefundPaymentIntentAsync(
        string paymentIntentId,
        long? amountMinor,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default);
}

public sealed record StripeCheckoutRequest(
    string Currency,
    long AmountMinor,
    string ProductName,
    string SuccessUrl,
    string CancelUrl,
    string ClientReferenceId,
    DateTime ExpiresAtUtc,
    IReadOnlyDictionary<string, string> Metadata);

public sealed record StripeCheckoutSession(string Id, string Url);

public sealed record StripeRefundResult(string Id, string Status, long AmountMinor);
