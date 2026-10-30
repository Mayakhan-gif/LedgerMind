using System.Globalization;
using System.Net;
using LedgerMind.Models;
using LedgerMind.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace LedgerMind.Tests.Email;

// POST /api/Email/invoice-reminder/{id}
public class InvoiceReminderTests : IClassFixture<TestWebApplicationFactory>
{
    private const string ReminderType = "InvoiceReminder";

    private readonly TestWebApplicationFactory _factory;

    public InvoiceReminderTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _factory.FakeEmail.FailSending = false;
    }

    private static string NewEmail() => $"customer-{Guid.NewGuid():N}@ledgermind.test";

    private static string Money(decimal amount) => amount.ToString("N2", CultureInfo.InvariantCulture);

    private static Task<HttpResponseMessage> SendReminderAsync(HttpClient client, int invoiceId) =>
        client.PostAsync($"/api/Email/invoice-reminder/{invoiceId}", null);

    private List<MimeKit.MimeMessage> SentFor(Invoice invoice) =>
        _factory.FakeEmail.Sent
            .Where(m => (m.Subject ?? string.Empty).Contains(invoice.InvoiceNumber))
            .ToList();

    private Task<List<EmailLog>> LogsForAsync(Invoice invoice)
    {
        var entityId = invoice.Id.ToString(CultureInfo.InvariantCulture);
        return _factory.WithDbAsync(db => db.EmailLogs
            .Where(l => l.Type == ReminderType && l.EntityId == entityId)
            .OrderBy(l => l.Id)
            .ToListAsync());
    }

    private Task<List<AuditLog>> ReminderAuditsAsync(Invoice invoice)
    {
        var entityId = invoice.Id.ToString(CultureInfo.InvariantCulture);
        return _factory.WithDbAsync(db => db.AuditLogs
            .Where(a => a.Action == "InvoiceReminderSent" && a.EntityType == "Invoice" && a.EntityId == entityId)
            .ToListAsync());
    }

    private async Task<(Customer Customer, Invoice Invoice)> ArrangeInvoiceAsync(
        string status = InvoiceStatus.Approved, decimal subtotal = 1000m, decimal paid = 0m, bool excluded = false)
    {
        var customer = await TestData.AddCustomerAsync(_factory, email: NewEmail());
        var invoice = await TestData.AddInvoiceAsync(_factory, customer.Id, status, subtotal: subtotal, isExcluded: excluded);
        if (paid != 0m)
            await TestData.AddPaymentAsync(_factory, invoice.Id, paid);
        return (customer, invoice);
    }

    [Fact]
    public async Task Reminder_for_approved_invoice_with_partial_payment_sends_email_logs_and_audits()
    {
        var (customer, invoice) = await ArrangeInvoiceAsync(InvoiceStatus.Approved, subtotal: 1000m, paid: 400m);
        var (manager, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);

        var response = await SendReminderAsync(client, invoice.Id);

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var json = await response.ReadJsonAsync();
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("message").GetString()));
        Assert.Equal(customer.Email, json.GetProperty("sentTo").GetString());
        Assert.Equal(600m, json.GetProperty("balance").GetDecimal());

        var message = Assert.Single(SentFor(invoice));
        Assert.Equal(customer.Email, message.To.Mailboxes.Single().Address);
        Assert.Contains(invoice.InvoiceNumber, message.Subject);
        Assert.Contains(Money(600m), message.HtmlBody);
        Assert.Contains(invoice.DueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), message.HtmlBody);

        var log = Assert.Single(await LogsForAsync(invoice));
        Assert.Equal("Sent", log.Status);
        Assert.Equal(customer.Email, log.ToEmail);
        Assert.Equal("Invoice", log.EntityType);

        var audit = Assert.Single(await ReminderAuditsAsync(invoice));
        Assert.Equal(manager.Id, audit.UserId);
    }

    [Fact]
    public async Task Partial_invoice_can_be_reminded_by_admin()
    {
        var (_, invoice) = await ArrangeInvoiceAsync(InvoiceStatus.Partial, subtotal: 500m, paid: 125m);
        var client = await TestAuth.AdminClientAsync(_factory);

        var response = await SendReminderAsync(client, invoice.Id);

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var message = Assert.Single(SentFor(invoice));
        Assert.Contains(Money(375m), message.HtmlBody);
    }

    [Fact]
    public async Task Second_reminder_within_24_hours_returns_400()
    {
        var (_, invoice) = await ArrangeInvoiceAsync();
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        await (await SendReminderAsync(client, invoice.Id)).AssertStatusAsync(HttpStatusCode.OK);

        var second = await SendReminderAsync(client, invoice.Id);

        await second.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.False(string.IsNullOrWhiteSpace(await second.ReadMessageAsync()));
        Assert.Single(SentFor(invoice));
        Assert.Single(await LogsForAsync(invoice));
        Assert.Single(await ReminderAuditsAsync(invoice));
    }

    [Fact]
    public async Task Two_reminders_at_the_same_time_send_only_one_email()
    {
        var (_, invoice) = await ArrangeInvoiceAsync(paid: 250m);
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);

        var responses = await Task.WhenAll(
            SendReminderAsync(client, invoice.Id),
            SendReminderAsync(client, invoice.Id));

        // Ek kamyab, doosra 24 ghante wali pabandi par ruk gaya
        var statuses = responses.Select(r => (int)r.StatusCode).OrderBy(s => s).ToArray();
        Assert.Equal(new[] { 200, 400 }, statuses);

        Assert.Single(SentFor(invoice));
        var log = Assert.Single(await LogsForAsync(invoice));
        Assert.Equal("Sent", log.Status);
        Assert.Single(await ReminderAuditsAsync(invoice));
    }

    [Fact]
    public async Task Reminder_sent_more_than_24_hours_ago_does_not_block_a_new_one()
    {
        var (customer, invoice) = await ArrangeInvoiceAsync();
        await _factory.WithDbAsync(async db =>
        {
            db.EmailLogs.Add(new EmailLog
            {
                ToEmail = customer.Email!,
                Subject = "old reminder",
                Type = ReminderType,
                EntityType = "Invoice",
                EntityId = invoice.Id.ToString(CultureInfo.InvariantCulture),
                Status = "Sent",
                CreatedAtUtc = DateTime.UtcNow.AddHours(-25)
            });
            await db.SaveChangesAsync();
        });
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);

        var response = await SendReminderAsync(client, invoice.Id);

        await response.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Single(SentFor(invoice));
    }

    [Fact]
    public async Task Smtp_failure_returns_502_logs_failed_and_retry_is_allowed()
    {
        var (_, invoice) = await ArrangeInvoiceAsync(paid: 100m);
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);

        _factory.FakeEmail.FailSending = true;
        HttpResponseMessage failed;
        try
        {
            failed = await SendReminderAsync(client, invoice.Id);
        }
        finally
        {
            _factory.FakeEmail.FailSending = false;
        }

        await failed.AssertStatusAsync(HttpStatusCode.BadGateway);
        Assert.False(string.IsNullOrWhiteSpace(await failed.ReadMessageAsync()));
        Assert.Empty(SentFor(invoice));
        Assert.Empty(await ReminderAuditsAsync(invoice));

        var failedLog = Assert.Single(await LogsForAsync(invoice));
        Assert.Equal("Failed", failedLog.Status);
        Assert.False(string.IsNullOrWhiteSpace(failedLog.Error));

        // Sirf "Sent" 24 ghante ki pabandi me ginta hai, is liye dobara koshish ho sakti hai
        var retry = await SendReminderAsync(client, invoice.Id);

        await retry.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Single(SentFor(invoice));
        var logs = await LogsForAsync(invoice);
        Assert.Equal(new[] { "Failed", "Sent" }, logs.Select(l => l.Status).ToArray());
        Assert.Single(await ReminderAuditsAsync(invoice));
    }

    [Fact]
    public async Task Customer_without_email_returns_400()
    {
        var customer = await TestData.AddCustomerAsync(_factory, email: null);
        var invoice = await TestData.AddInvoiceAsync(_factory, customer.Id, InvoiceStatus.Approved);
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);

        var response = await SendReminderAsync(client, invoice.Id);

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.False(string.IsNullOrWhiteSpace(await response.ReadMessageAsync()));
        Assert.Empty(SentFor(invoice));
        Assert.Empty(await LogsForAsync(invoice));
    }

    [Fact]
    public async Task Customer_with_invalid_email_returns_400()
    {
        var customer = await TestData.AddCustomerAsync(_factory, email: "not-an-email");
        var invoice = await TestData.AddInvoiceAsync(_factory, customer.Id, InvoiceStatus.Approved);
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);

        var response = await SendReminderAsync(client, invoice.Id);

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Empty(await LogsForAsync(invoice));
    }

    [Fact]
    public async Task Excluded_invoice_returns_400()
    {
        var (_, invoice) = await ArrangeInvoiceAsync(InvoiceStatus.Approved, excluded: true);
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);

        var response = await SendReminderAsync(client, invoice.Id);

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Empty(SentFor(invoice));
        Assert.Empty(await LogsForAsync(invoice));
    }

    [Theory]
    [InlineData(InvoiceStatus.Draft)]
    [InlineData(InvoiceStatus.PendingApproval)]
    [InlineData(InvoiceStatus.Rejected)]
    [InlineData(InvoiceStatus.Paid)]
    [InlineData(InvoiceStatus.Cancelled)]
    public async Task Invoice_that_is_not_collectable_returns_400(string status)
    {
        var (_, invoice) = await ArrangeInvoiceAsync(status);
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);

        var response = await SendReminderAsync(client, invoice.Id);

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Empty(SentFor(invoice));
        Assert.Empty(await LogsForAsync(invoice));
    }

    [Fact]
    public async Task Invoice_with_nothing_left_to_pay_returns_400()
    {
        // Status abhi Approved hai magar payments poori raqam cover karti hain
        var (_, invoice) = await ArrangeInvoiceAsync(InvoiceStatus.Approved, subtotal: 800m, paid: 800m);
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);

        var response = await SendReminderAsync(client, invoice.Id);

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Empty(SentFor(invoice));
    }

    [Fact]
    public async Task Unknown_invoice_returns_404()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);

        var response = await SendReminderAsync(client, int.MaxValue);

        await response.AssertStatusAsync(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Customer_name_and_invoice_number_are_html_encoded()
    {
        var customer = await TestData.AddCustomerAsync(_factory, name: "<b>Evil & Co</b>", email: NewEmail());
        var invoice = await TestData.AddInvoiceAsync(_factory, customer.Id, InvoiceStatus.Approved);
        var client = await TestAuth.AdminClientAsync(_factory);

        var response = await SendReminderAsync(client, invoice.Id);

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var message = Assert.Single(SentFor(invoice));
        Assert.Contains("&lt;b&gt;Evil &amp; Co&lt;/b&gt;", message.HtmlBody);
        Assert.DoesNotContain("<b>Evil", message.HtmlBody);
    }

    [Fact]
    public async Task Employee_gets_403()
    {
        var (_, invoice) = await ArrangeInvoiceAsync();
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);

        var response = await SendReminderAsync(client, invoice.Id);

        await response.AssertStatusAsync(HttpStatusCode.Forbidden);
        Assert.Empty(SentFor(invoice));
        Assert.Empty(await LogsForAsync(invoice));
    }

    [Fact]
    public async Task Anonymous_gets_401()
    {
        var (_, invoice) = await ArrangeInvoiceAsync();
        var client = _factory.CreateClient();

        var response = await SendReminderAsync(client, invoice.Id);

        await response.AssertStatusAsync(HttpStatusCode.Unauthorized);
        Assert.Empty(SentFor(invoice));
    }
}
