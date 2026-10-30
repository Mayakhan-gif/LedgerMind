using System.Collections.Concurrent;
using LedgerMind.Services;

namespace LedgerMind.Tests.Infrastructure.Fakes;

// Asli Stripe API ki jagah: har call yaad rakhta hai, session/refund ids khud banata hai.
public sealed class FakeStripeGateway : IStripeGateway
{
    private int _counter;

    public ConcurrentQueue<StripeCheckoutRequest> CreatedSessions { get; } = new();
    public ConcurrentQueue<string> ExpiredSessionIds { get; } = new();
    public ConcurrentQueue<(string PaymentIntentId, long? AmountMinor)> Refunds { get; } = new();
    public ConcurrentQueue<string?> RefundIdempotencyKeys { get; } = new();

    // Agle call par ye exception phenko (null = normal)
    public Exception? ThrowOnCreate { get; set; }
    public Exception? ThrowOnExpire { get; set; }
    public Exception? ThrowOnRefund { get; set; }

    // Refund ka jawab "succeeded" ke ilawa chahiye ho to
    public string RefundStatus { get; set; } = "succeeded";

    public string? LastSessionId { get; private set; }

    public Task<StripeCheckoutSession> CreateCheckoutSessionAsync(
        StripeCheckoutRequest request, CancellationToken cancellationToken = default)
    {
        if (ThrowOnCreate != null)
            throw ThrowOnCreate;

        CreatedSessions.Enqueue(request);
        var id = $"cs_test_{Interlocked.Increment(ref _counter)}_{Guid.NewGuid():N}";
        LastSessionId = id;
        return Task.FromResult(new StripeCheckoutSession(id, $"https://checkout.stripe.com/c/pay/{id}"));
    }

    public Task ExpireCheckoutSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (ThrowOnExpire != null)
            throw ThrowOnExpire;

        ExpiredSessionIds.Enqueue(sessionId);
        return Task.CompletedTask;
    }

    public Task<StripeRefundResult> RefundPaymentIntentAsync(
        string paymentIntentId,
        long? amountMinor,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        if (ThrowOnRefund != null)
            throw ThrowOnRefund;

        Refunds.Enqueue((paymentIntentId, amountMinor));
        RefundIdempotencyKeys.Enqueue(idempotencyKey);
        return Task.FromResult(new StripeRefundResult(
            $"re_test_{Interlocked.Increment(ref _counter)}", RefundStatus, amountMinor ?? 0));
    }

    public void Reset()
    {
        while (CreatedSessions.TryDequeue(out _)) { }
        while (ExpiredSessionIds.TryDequeue(out _)) { }
        while (Refunds.TryDequeue(out _)) { }
        while (RefundIdempotencyKeys.TryDequeue(out _)) { }
        ThrowOnCreate = null;
        ThrowOnExpire = null;
        ThrowOnRefund = null;
        RefundStatus = "succeeded";
        LastSessionId = null;
    }
}
