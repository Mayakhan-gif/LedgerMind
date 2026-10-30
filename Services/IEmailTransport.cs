using MimeKit;

namespace LedgerMind.Services;

// Email bhejne ka asal rasta (SMTP). Tests me fake lagta hai.
public interface IEmailTransport
{
    Task SendAsync(MimeMessage message, CancellationToken cancellationToken = default);
}
