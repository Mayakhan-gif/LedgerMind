using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LedgerMind.Models;
using LedgerMind.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace LedgerMind.Tests.Payments;

public class PaymentsControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private const string MissingInvoiceMessage = "Is InvoiceId ka invoice maujood nahi.";
    private const string NotCollectableMessage = "Payment sirf Approved ya Partial invoice par ho sakti hai.";
    private const string StripeDeleteMessage =
        "Stripe payment ya refund yahan se delete nahi hoti, refund ka rasta use karein.";
    private const string NeedsReviewMessage = "Is invoice ki online payment review me hai, pehle usay nipta lein.";
    private const string DuplicateMessage =
        "Yehi payment abhi abhi darj hui hai. Dobara darj karni ho to ek minute baad koshish karein.";

    private readonly TestWebApplicationFactory _factory;

    public PaymentsControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // ---------- Helpers ----------

    private Task<(TestUser User, HttpClient Client)> ManagerAsync() =>
        TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);

    private async Task<Invoice> NewInvoiceAsync(
        string status = InvoiceStatus.Approved,
        decimal total = 1000m,
        string currency = "PKR",
        bool excluded = false)
    {
        var customer = await TestData.AddCustomerAsync(_factory);
        return await TestData.AddInvoiceAsync(
            _factory, customer.Id, status, subtotal: total, currency: currency, isExcluded: excluded);
    }

    private static Task<HttpResponseMessage> PayAsync(
        HttpClient client, int invoiceId, decimal amount, string? method = "Cash", string? note = null) =>
        client.PostAsJsonAsync("/api/Payments", new
        {
            invoiceId,
            amount,
            paymentDate = DateTime.UtcNow.Date,
            method,
            note
        });

    private static async Task<int> PayAndGetIdAsync(HttpClient client, int invoiceId, decimal amount)
    {
        var response = await PayAsync(client, invoiceId, amount);
        await response.AssertStatusAsync(HttpStatusCode.OK);
        var json = await response.ReadJsonAsync();
        return json.GetProperty("id").GetInt32();
    }

    private Task<Invoice> LoadInvoiceAsync(int invoiceId) =>
        _factory.WithDbAsync(db => db.Invoices.AsNoTracking().SingleAsync(i => i.Id == invoiceId));

    private Task<List<Payment>> LoadPaymentsAsync(int invoiceId) =>
        _factory.WithDbAsync(db => db.Payments
            .AsNoTracking()
            .Where(p => p.InvoiceId == invoiceId)
            .OrderBy(p => p.Id)
            .ToListAsync());

    private Task<List<AuditLog>> LoadAuditAsync(string action, int invoiceId)
    {
        var entityId = invoiceId.ToString();
        return _factory.WithDbAsync(db => db.AuditLogs
            .AsNoTracking()
            .Where(a => a.Action == action && a.EntityType == "Invoice" && a.EntityId == entityId)
            .ToListAsync());
    }

    // ---------- POST /api/Payments ----------

    [Fact]
    public async Task Partial_payment_moves_invoice_to_Partial_and_returns_the_payment()
    {
        var (manager, client) = await ManagerAsync();
        var invoice = await NewInvoiceAsync(total: 1000m);

        var response = await PayAsync(client, invoice.Id, 400m, method: "Bank", note: "pehli qist");

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var json = await response.ReadJsonAsync();
        Assert.True(json.GetProperty("id").GetInt32() > 0);
        Assert.Equal(invoice.Id, json.GetProperty("invoiceId").GetInt32());
        Assert.Equal(400m, json.GetProperty("amount").GetDecimal());
        Assert.Equal("Bank", json.GetProperty("method").GetString());
        Assert.Equal("pehli qist", json.GetProperty("note").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("paymentAttemptId").ValueKind);
        Assert.Equal(manager.Id, json.GetProperty("createdByUserId").GetString());

        Assert.Equal(InvoiceStatus.Partial, (await LoadInvoiceAsync(invoice.Id)).Status);

        var payment = Assert.Single(await LoadPaymentsAsync(invoice.Id));
        Assert.Equal(400m, payment.Amount);
        Assert.Equal(manager.Id, payment.CreatedByUserId);
        Assert.Null(payment.PaymentAttemptId);
    }

    [Fact]
    public async Task Full_payment_moves_invoice_to_Paid()
    {
        var (_, client) = await ManagerAsync();
        var invoice = await NewInvoiceAsync(total: 1000m);

        var response = await PayAsync(client, invoice.Id, 1000m);

        await response.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Equal(InvoiceStatus.Paid, (await LoadInvoiceAsync(invoice.Id)).Status);
    }

    [Fact]
    public async Task Payments_that_add_up_to_total_end_in_Paid()
    {
        var (_, client) = await ManagerAsync();
        var invoice = await NewInvoiceAsync(total: 1000m);

        await PayAndGetIdAsync(client, invoice.Id, 600m);
        Assert.Equal(InvoiceStatus.Partial, (await LoadInvoiceAsync(invoice.Id)).Status);

        await PayAndGetIdAsync(client, invoice.Id, 400m);
        Assert.Equal(InvoiceStatus.Paid, (await LoadInvoiceAsync(invoice.Id)).Status);

        Assert.Equal(2, (await LoadPaymentsAsync(invoice.Id)).Count);
    }

    [Fact]
    public async Task Paying_exactly_the_remaining_balance_is_allowed()
    {
        var (_, client) = await ManagerAsync();
        var invoice = await NewInvoiceAsync(status: InvoiceStatus.Partial, total: 1000m);
        await TestData.AddPaymentAsync(_factory, invoice.Id, 250.50m);

        var response = await PayAsync(client, invoice.Id, 749.50m);

        await response.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Equal(InvoiceStatus.Paid, (await LoadInvoiceAsync(invoice.Id)).Status);
    }

    [Fact]
    public async Task Overpayment_is_rejected_and_message_contains_the_balance()
    {
        var (_, client) = await ManagerAsync();
        var invoice = await NewInvoiceAsync(status: InvoiceStatus.Partial, total: 1000m);
        await TestData.AddPaymentAsync(_factory, invoice.Id, 300m);

        var response = await PayAsync(client, invoice.Id, 800m);

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        var json = await response.ReadJsonAsync();
        var message = json.GetProperty("message").GetString();
        Assert.NotNull(message);
        Assert.Contains("700.00", message);
        Assert.Equal(700m, json.GetProperty("balance").GetDecimal());
        Assert.Equal("PKR", json.GetProperty("currency").GetString());

        Assert.Single(await LoadPaymentsAsync(invoice.Id));
        Assert.Equal(InvoiceStatus.Partial, (await LoadInvoiceAsync(invoice.Id)).Status);
        Assert.Empty(await LoadAuditAsync("PaymentRecorded", invoice.Id));
    }

    [Fact]
    public async Task Overpayment_by_one_cent_is_rejected()
    {
        var (_, client) = await ManagerAsync();
        var invoice = await NewInvoiceAsync(total: 500m);

        var response = await PayAsync(client, invoice.Id, 500.01m);

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Contains("500.00", await response.ReadMessageAsync());
        Assert.Empty(await LoadPaymentsAsync(invoice.Id));
        Assert.Equal(InvoiceStatus.Approved, (await LoadInvoiceAsync(invoice.Id)).Status);
    }

    [Fact]
    public async Task Payment_on_excluded_invoice_is_rejected()
    {
        var (_, client) = await ManagerAsync();
        var invoice = await NewInvoiceAsync(status: InvoiceStatus.Approved, excluded: true);

        var response = await PayAsync(client, invoice.Id, 100m);

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.False(string.IsNullOrWhiteSpace(await response.ReadMessageAsync()));
        Assert.Empty(await LoadPaymentsAsync(invoice.Id));
        Assert.Equal(InvoiceStatus.Approved, (await LoadInvoiceAsync(invoice.Id)).Status);
    }

    [Theory]
    [InlineData(InvoiceStatus.Draft)]
    [InlineData(InvoiceStatus.PendingApproval)]
    [InlineData(InvoiceStatus.Rejected)]
    [InlineData(InvoiceStatus.Cancelled)]
    [InlineData(InvoiceStatus.Paid)]
    public async Task Payment_on_non_collectable_invoice_is_rejected(string status)
    {
        var (_, client) = await ManagerAsync();
        var invoice = await NewInvoiceAsync(status: status, total: 1000m);

        var response = await PayAsync(client, invoice.Id, 100m);

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Equal(NotCollectableMessage, await response.ReadMessageAsync());
        Assert.Empty(await LoadPaymentsAsync(invoice.Id));
        Assert.Equal(status, (await LoadInvoiceAsync(invoice.Id)).Status);
    }

    [Fact]
    public async Task Payment_for_missing_invoice_is_rejected()
    {
        var (_, client) = await ManagerAsync();

        var response = await PayAsync(client, 987654321, 100m);

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Equal(MissingInvoiceMessage, await response.ReadMessageAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-50)]
    [InlineData(10.005)]
    public async Task Invalid_amount_is_rejected_by_validation(double amount)
    {
        var (_, client) = await ManagerAsync();
        var invoice = await NewInvoiceAsync(total: 1000m);

        var response = await PayAsync(client, invoice.Id, (decimal)amount);

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Empty(await LoadPaymentsAsync(invoice.Id));
        Assert.Equal(InvoiceStatus.Approved, (await LoadInvoiceAsync(invoice.Id)).Status);
    }

    [Fact]
    public async Task Missing_payment_date_is_rejected_by_validation()
    {
        var (_, client) = await ManagerAsync();
        var invoice = await NewInvoiceAsync(total: 1000m);

        var response = await client.PostAsJsonAsync("/api/Payments", new { invoiceId = invoice.Id, amount = 100m });

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Empty(await LoadPaymentsAsync(invoice.Id));
    }

    [Theory]
    [InlineData("  Bank Transfer  ", "Bank Transfer")]
    [InlineData(null, "Cash")]
    [InlineData("   ", "Cash")]
    public async Task Method_is_trimmed_and_blank_defaults_to_Cash(string? method, string expected)
    {
        var (_, client) = await ManagerAsync();
        var invoice = await NewInvoiceAsync(total: 1000m);

        var response = await PayAsync(client, invoice.Id, 100m, method: method);

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var json = await response.ReadJsonAsync();
        Assert.Equal(expected, json.GetProperty("method").GetString());
        Assert.Equal(expected, Assert.Single(await LoadPaymentsAsync(invoice.Id)).Method);
    }

    [Fact]
    public async Task Admin_can_record_payment()
    {
        var client = await TestAuth.AdminClientAsync(_factory);
        var invoice = await NewInvoiceAsync(total: 1000m);

        var response = await PayAsync(client, invoice.Id, 100m);

        await response.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Single(await LoadPaymentsAsync(invoice.Id));
    }

    [Fact]
    public async Task Employee_cannot_record_payment()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var invoice = await NewInvoiceAsync(total: 1000m);

        var response = await PayAsync(client, invoice.Id, 100m);

        await response.AssertStatusAsync(HttpStatusCode.Forbidden);
        Assert.Empty(await LoadPaymentsAsync(invoice.Id));
        Assert.Equal(InvoiceStatus.Approved, (await LoadInvoiceAsync(invoice.Id)).Status);
    }

    [Fact]
    public async Task Anonymous_cannot_record_payment()
    {
        var client = _factory.CreateClient();
        var invoice = await NewInvoiceAsync(total: 1000m);

        var response = await PayAsync(client, invoice.Id, 100m);

        await response.AssertStatusAsync(HttpStatusCode.Unauthorized);
        Assert.Empty(await LoadPaymentsAsync(invoice.Id));
    }

    [Fact]
    public async Task Recording_payment_writes_PaymentRecorded_audit()
    {
        var (manager, client) = await ManagerAsync();
        var invoice = await NewInvoiceAsync(total: 500m);

        var response = await PayAsync(client, invoice.Id, 200m);

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var audit = Assert.Single(await LoadAuditAsync("PaymentRecorded", invoice.Id));
        Assert.Equal(manager.Id, audit.UserId);
        Assert.Contains(invoice.InvoiceNumber, audit.Details);
        Assert.Contains("200.00", audit.Details);
    }

    // ---------- Online payment ke saath takraav ----------

    private Task<PaymentAttempt> LoadAttemptAsync(int attemptId) =>
        _factory.WithDbAsync(db => db.PaymentAttempts.AsNoTracking().SingleAsync(a => a.Id == attemptId));

    [Fact]
    public async Task Payment_is_blocked_while_an_online_payment_needs_review()
    {
        var (_, client) = await ManagerAsync();
        var invoice = await NewInvoiceAsync(total: 1000m);
        await TestData.AddPaymentAttemptAsync(_factory, invoice.Id, 1000m, status: PaymentAttemptStatus.NeedsReview);

        var response = await PayAsync(client, invoice.Id, 1000m);

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Equal(NeedsReviewMessage, await response.ReadMessageAsync());
        Assert.Empty(await LoadPaymentsAsync(invoice.Id));
        Assert.Equal(InvoiceStatus.Approved, (await LoadInvoiceAsync(invoice.Id)).Status);
        Assert.Empty(await LoadAuditAsync("PaymentRecorded", invoice.Id));
    }

    [Fact]
    public async Task Payment_closes_open_stripe_checkout_and_audits_it()
    {
        var (_, client) = await ManagerAsync();
        var invoice = await NewInvoiceAsync(total: 1000m);
        var open = await TestData.AddPaymentAttemptAsync(_factory, invoice.Id, 1000m);
        var closed = await TestData.AddPaymentAttemptAsync(
            _factory, invoice.Id, 1000m, status: PaymentAttemptStatus.Expired);

        var response = await PayAsync(client, invoice.Id, 1000m);

        await response.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Equal(InvoiceStatus.Paid, (await LoadInvoiceAsync(invoice.Id)).Status);
        Assert.Contains(open.ProviderSessionId, _factory.FakeStripe.ExpiredSessionIds);
        Assert.DoesNotContain(closed.ProviderSessionId, _factory.FakeStripe.ExpiredSessionIds);

        var saved = await LoadAttemptAsync(open.Id);
        Assert.Equal(PaymentAttemptStatus.Expired, saved.Status);
        Assert.NotNull(saved.CompletedAtUtc);
        Assert.False(string.IsNullOrWhiteSpace(saved.FailureReason));

        var audit = Assert.Single(await LoadAuditAsync("PaymentRecorded", invoice.Id));
        Assert.Contains("1 Stripe checkout band", audit.Details);
    }

    [Fact]
    public async Task Partial_payment_also_closes_open_checkout_because_its_amount_is_stale()
    {
        var (_, client) = await ManagerAsync();
        var invoice = await NewInvoiceAsync(total: 1000m);
        var open = await TestData.AddPaymentAttemptAsync(_factory, invoice.Id, 1000m);

        var response = await PayAsync(client, invoice.Id, 300m);

        await response.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Equal(InvoiceStatus.Partial, (await LoadInvoiceAsync(invoice.Id)).Status);
        Assert.Equal(PaymentAttemptStatus.Expired, (await LoadAttemptAsync(open.Id)).Status);
    }

    [Fact]
    public async Task Payment_is_recorded_when_stripe_session_is_already_closed()
    {
        var (_, client) = await ManagerAsync();
        var invoice = await NewInvoiceAsync(total: 1000m);
        var open = await TestData.AddPaymentAttemptAsync(_factory, invoice.Id, 1000m);

        _factory.FakeStripe.ThrowOnExpire = new global::Stripe.StripeException("session already expired");
        HttpResponseMessage response;
        try
        {
            response = await PayAsync(client, invoice.Id, 1000m);
        }
        finally
        {
            _factory.FakeStripe.ThrowOnExpire = null;
        }

        await response.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Single(await LoadPaymentsAsync(invoice.Id));
        Assert.Equal(InvoiceStatus.Paid, (await LoadInvoiceAsync(invoice.Id)).Status);
        Assert.Equal(PaymentAttemptStatus.Expired, (await LoadAttemptAsync(open.Id)).Status);
    }

    // ---------- Double submit ----------

    [Fact]
    public async Task Same_payment_submitted_twice_returns_409_and_is_recorded_once()
    {
        var (_, client) = await ManagerAsync();
        var invoice = await NewInvoiceAsync(total: 1000m);
        var body = new
        {
            invoiceId = invoice.Id,
            amount = 200m,
            paymentDate = new DateTime(2026, 10, 1),
            method = "Bank",
            note = "qist"
        };

        var first = await client.PostAsJsonAsync("/api/Payments", body);
        var second = await client.PostAsJsonAsync("/api/Payments", body);

        await first.AssertStatusAsync(HttpStatusCode.OK);
        await second.AssertStatusAsync(HttpStatusCode.Conflict);
        Assert.Equal(DuplicateMessage, await second.ReadMessageAsync());
        Assert.Equal(200m, Assert.Single(await LoadPaymentsAsync(invoice.Id)).Amount);
        Assert.Single(await LoadAuditAsync("PaymentRecorded", invoice.Id));
    }

    [Fact]
    public async Task Same_amount_with_a_different_note_or_by_another_manager_is_not_a_duplicate()
    {
        var (_, client) = await ManagerAsync();
        var (_, otherClient) = await ManagerAsync();
        var invoice = await NewInvoiceAsync(total: 1000m);

        var first = await PayAsync(client, invoice.Id, 100m, note: "cheque 1");
        var secondNote = await PayAsync(client, invoice.Id, 100m, note: "cheque 2");
        var otherManager = await PayAsync(otherClient, invoice.Id, 100m, note: "cheque 1");

        await first.AssertStatusAsync(HttpStatusCode.OK);
        await secondNote.AssertStatusAsync(HttpStatusCode.OK);
        await otherManager.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Equal(300m, (await LoadPaymentsAsync(invoice.Id)).Sum(p => p.Amount));
    }

    // ---------- GET /api/Payments/invoice/{id} ----------

    [Fact]
    public async Task Get_by_invoice_returns_totals_balance_currency_and_payments()
    {
        var (_, client) = await ManagerAsync();
        var invoice = await NewInvoiceAsync(status: InvoiceStatus.Partial, total: 1000m, currency: "USD");
        await TestData.AddPaymentAsync(_factory, invoice.Id, 300m);
        await TestData.AddPaymentAsync(_factory, invoice.Id, 200m);
        // Refund ki reversal entry (manfi)
        await TestData.AddPaymentAsync(_factory, invoice.Id, -100m, method: "Stripe");

        var response = await client.GetAsync($"/api/Payments/invoice/{invoice.Id}");

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var json = await response.ReadJsonAsync();
        Assert.Equal(invoice.Id, json.GetProperty("invoiceId").GetInt32());
        Assert.Equal(1000m, json.GetProperty("invoiceAmount").GetDecimal());
        Assert.Equal(400m, json.GetProperty("totalPaid").GetDecimal());
        Assert.Equal(600m, json.GetProperty("balance").GetDecimal());
        Assert.Equal("USD", json.GetProperty("currency").GetString());
        Assert.Equal(InvoiceStatus.Partial, json.GetProperty("status").GetString());
        Assert.Equal(invoice.InvoiceNumber, json.GetProperty("invoiceNumber").GetString());

        var payments = json.GetProperty("payments");
        Assert.Equal(3, payments.GetArrayLength());
        Assert.All(payments.EnumerateArray(), p => Assert.Equal(invoice.Id, p.GetProperty("invoiceId").GetInt32()));
    }

    [Fact]
    public async Task Get_by_invoice_without_payments_has_full_balance()
    {
        var (_, client) = await ManagerAsync();
        var invoice = await NewInvoiceAsync(total: 750m, currency: "EUR");

        var response = await client.GetAsync($"/api/Payments/invoice/{invoice.Id}");

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var json = await response.ReadJsonAsync();
        Assert.Equal(0m, json.GetProperty("totalPaid").GetDecimal());
        Assert.Equal(750m, json.GetProperty("balance").GetDecimal());
        Assert.Equal("EUR", json.GetProperty("currency").GetString());
        Assert.Equal(0, json.GetProperty("payments").GetArrayLength());
    }

    [Fact]
    public async Task Get_by_missing_invoice_returns_404()
    {
        var (_, client) = await ManagerAsync();

        var response = await client.GetAsync("/api/Payments/invoice/987654321");

        await response.AssertStatusAsync(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Employee_cannot_read_invoice_payments()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var invoice = await NewInvoiceAsync(total: 1000m);

        var response = await client.GetAsync($"/api/Payments/invoice/{invoice.Id}");

        await response.AssertStatusAsync(HttpStatusCode.Forbidden);
    }

    // ---------- DELETE /api/Payments/{id} ----------

    [Fact]
    public async Task Deleting_manual_payments_recomputes_status_from_Paid_to_Partial_to_Approved()
    {
        var (_, client) = await ManagerAsync();
        var invoice = await NewInvoiceAsync(total: 1000m);

        var firstId = await PayAndGetIdAsync(client, invoice.Id, 600m);
        var secondId = await PayAndGetIdAsync(client, invoice.Id, 400m);
        Assert.Equal(InvoiceStatus.Paid, (await LoadInvoiceAsync(invoice.Id)).Status);

        var deleteSecond = await client.DeleteAsync($"/api/Payments/{secondId}");
        await deleteSecond.AssertStatusAsync(HttpStatusCode.NoContent);
        Assert.Equal(InvoiceStatus.Partial, (await LoadInvoiceAsync(invoice.Id)).Status);
        Assert.Equal(firstId, Assert.Single(await LoadPaymentsAsync(invoice.Id)).Id);

        var deleteFirst = await client.DeleteAsync($"/api/Payments/{firstId}");
        await deleteFirst.AssertStatusAsync(HttpStatusCode.NoContent);
        Assert.Equal(InvoiceStatus.Approved, (await LoadInvoiceAsync(invoice.Id)).Status);
        Assert.Empty(await LoadPaymentsAsync(invoice.Id));
    }

    [Fact]
    public async Task Deleting_payment_writes_PaymentDeleted_audit()
    {
        var (manager, client) = await ManagerAsync();
        var invoice = await NewInvoiceAsync(total: 1000m);
        var paymentId = await PayAndGetIdAsync(client, invoice.Id, 300m);

        var response = await client.DeleteAsync($"/api/Payments/{paymentId}");

        await response.AssertStatusAsync(HttpStatusCode.NoContent);
        var audit = Assert.Single(await LoadAuditAsync("PaymentDeleted", invoice.Id));
        Assert.Equal(manager.Id, audit.UserId);
        Assert.Contains($"#{paymentId}", audit.Details);
    }

    [Fact]
    public async Task Deleting_stripe_linked_payment_is_rejected()
    {
        var (_, client) = await ManagerAsync();
        var invoice = await NewInvoiceAsync(status: InvoiceStatus.Paid, total: 1000m);
        var attempt = await TestData.AddPaymentAttemptAsync(
            _factory, invoice.Id, 1000m, status: PaymentAttemptStatus.Succeeded);
        var payment = await TestData.AddPaymentAsync(
            _factory, invoice.Id, 1000m, method: "Stripe", paymentAttemptId: attempt.Id);

        var response = await client.DeleteAsync($"/api/Payments/{payment.Id}");

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Equal(StripeDeleteMessage, await response.ReadMessageAsync());
        Assert.Single(await LoadPaymentsAsync(invoice.Id));
        Assert.Equal(InvoiceStatus.Paid, (await LoadInvoiceAsync(invoice.Id)).Status);
        Assert.Empty(await LoadAuditAsync("PaymentDeleted", invoice.Id));
    }

    [Fact]
    public async Task Deleting_negative_refund_reversal_is_rejected()
    {
        var (_, client) = await ManagerAsync();
        var invoice = await NewInvoiceAsync(status: InvoiceStatus.Partial, total: 1000m);
        await TestData.AddPaymentAsync(_factory, invoice.Id, 1000m);
        var reversal = await TestData.AddPaymentAsync(_factory, invoice.Id, -400m, method: "Stripe");

        var response = await client.DeleteAsync($"/api/Payments/{reversal.Id}");

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Equal(StripeDeleteMessage, await response.ReadMessageAsync());
        Assert.Equal(2, (await LoadPaymentsAsync(invoice.Id)).Count);
        Assert.Equal(InvoiceStatus.Partial, (await LoadInvoiceAsync(invoice.Id)).Status);
    }

    [Theory]
    [InlineData(InvoiceStatus.Cancelled)]
    [InlineData(InvoiceStatus.Draft)]
    [InlineData(InvoiceStatus.Rejected)]
    public async Task Deleting_payment_of_invoice_in_wrong_status_is_rejected(string status)
    {
        var (_, client) = await ManagerAsync();
        var invoice = await NewInvoiceAsync(status: status, total: 1000m);
        var payment = await TestData.AddPaymentAsync(_factory, invoice.Id, 100m);

        var response = await client.DeleteAsync($"/api/Payments/{payment.Id}");

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Single(await LoadPaymentsAsync(invoice.Id));
        Assert.Equal(status, (await LoadInvoiceAsync(invoice.Id)).Status);
    }

    [Fact]
    public async Task Deleting_missing_payment_returns_404()
    {
        var (_, client) = await ManagerAsync();

        var response = await client.DeleteAsync("/api/Payments/987654321");

        await response.AssertStatusAsync(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Employee_cannot_delete_payment()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var invoice = await NewInvoiceAsync(status: InvoiceStatus.Partial, total: 1000m);
        var payment = await TestData.AddPaymentAsync(_factory, invoice.Id, 100m);

        var response = await client.DeleteAsync($"/api/Payments/{payment.Id}");

        await response.AssertStatusAsync(HttpStatusCode.Forbidden);
        Assert.Single(await LoadPaymentsAsync(invoice.Id));
        Assert.Equal(InvoiceStatus.Partial, (await LoadInvoiceAsync(invoice.Id)).Status);
    }
}
