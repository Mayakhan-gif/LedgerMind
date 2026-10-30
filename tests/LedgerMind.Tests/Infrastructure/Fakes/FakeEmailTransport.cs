using System.Collections.Concurrent;
using LedgerMind.Services;
using MimeKit;

namespace LedgerMind.Tests.Infrastructure.Fakes;

// SMTP ki jagah: bheji gayi emails yaad rakhta hai, ya zaroorat ho to fail hota hai.
public sealed class FakeEmailTransport : IEmailTransport
{
    public ConcurrentQueue<MimeMessage> Sent { get; } = new();

    public bool FailSending { get; set; }

    public Task SendAsync(MimeMessage message, CancellationToken cancellationToken = default)
    {
        if (FailSending)
            throw new InvalidOperationException("Fake SMTP failure");

        Sent.Enqueue(message);
        return Task.CompletedTask;
    }

    public void Reset()
    {
        while (Sent.TryDequeue(out _)) { }
        FailSending = false;
    }
}
