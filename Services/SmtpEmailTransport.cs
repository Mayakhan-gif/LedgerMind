using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace LedgerMind.Services;

public class SmtpEmailTransport : IEmailTransport
{
    private readonly IConfiguration _config;

    public SmtpEmailTransport(IConfiguration config)
    {
        _config = config;
    }

    public async Task SendAsync(MimeMessage message, CancellationToken cancellationToken = default)
    {
        var host = _config["Smtp:Host"] ?? "localhost";
        var port = _config.GetValue("Smtp:Port", 25);
        var useStartTls = _config.GetValue("Smtp:UseStartTls", false);

        using var client = new SmtpClient();
        await client.ConnectAsync(host, port,
            useStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.None,
            cancellationToken);

        var username = _config["Smtp:Username"];
        if (!string.IsNullOrWhiteSpace(username))
            await client.AuthenticateAsync(username, _config["Smtp:Password"] ?? string.Empty, cancellationToken);

        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
    }
}
