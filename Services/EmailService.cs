using LedgerMind.Data;
using LedgerMind.Models;
using MimeKit;

namespace LedgerMind.Services;

public class EmailService
{
    private const int ErrorLength = 500;

    private readonly IConfiguration _config;
    private readonly AppDbContext _db;
    private readonly IEmailTransport _transport;
    private readonly ILogger<EmailService> _logger;

    public EmailService(
        IConfiguration config,
        AppDbContext db,
        IEmailTransport transport,
        ILogger<EmailService> logger)
    {
        _config = config;
        _db = db;
        _transport = transport;
        _logger = logger;
    }

    // true = email chali gayi, false = fail (wajah EmailLogs me likhi jati hai)
    public async Task<bool> SendAsync(
        string to, string subject, string htmlBody,
        string type, string? entityType = null, string? entityId = null)
    {
        var log = new EmailLog
        {
            ToEmail = to,
            Subject = subject,
            Type = type,
            EntityType = entityType,
            EntityId = entityId
        };

        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(
                _config["Smtp:FromName"] ?? "LedgerMind",
                _config["Smtp:FromAddress"] ?? "noreply@ledgermind.local"));
            message.To.Add(MailboxAddress.Parse(to));
            message.Subject = subject;
            message.Body = new BodyBuilder { HtmlBody = htmlBody }.ToMessageBody();

            await _transport.SendAsync(message);

            log.Status = "Sent";
        }
        catch (Exception ex)
        {
            log.Status = "Failed";
            log.Error = ex.Message.Length > ErrorLength ? ex.Message[..ErrorLength] : ex.Message;
            _logger.LogWarning("Email fail hui ({Type}): {Error}", type, log.Error);
        }

        _db.EmailLogs.Add(log);
        await _db.SaveChangesAsync();

        return log.Status == "Sent";
    }
}
