using System.Net;
using System.Net.Http.Json;
using LedgerMind.Models;
using LedgerMind.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace LedgerMind.Tests.Invoicing;

// Create / update / submit / delete: validation, ownership aur audit
public class InvoiceCrudTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public InvoiceCrudTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private Task<bool> InvoiceNumberExistsAsync(string number) =>
        _factory.WithDbAsync(db => db.Invoices.AnyAsync(i => i.InvoiceNumber == number));

    // ---------- Read ----------

    [Fact]
    public async Task Get_without_token_returns_401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/Invoices");

        await response.AssertStatusAsync(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Employee_can_list_own_invoices_with_customer_object()
    {
        var (employee, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var customer = await TestData.AddCustomerAsync(_factory);
        var invoice = await TestData.AddInvoiceAsync(_factory, customer.Id, createdByUserId: employee.Id);

        var response = await client.GetAsync("/api/Invoices");

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var json = await response.ReadJsonAsync();
        var row = json.EnumerateArray().Single(e => e.GetProperty("id").GetInt32() == invoice.Id);
        Assert.Equal(customer.Name, row.GetProperty("customer").GetProperty("name").GetString());
        Assert.Equal(invoice.InvoiceNumber, row.GetProperty("invoiceNumber").GetString());
    }

    [Fact]
    public async Task Get_by_id_returns_invoice_and_404_for_unknown()
    {
        var (employee, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var customer = await TestData.AddCustomerAsync(_factory);
        var invoice = await TestData.AddInvoiceAsync(_factory, customer.Id, createdByUserId: employee.Id);

        var found = await client.GetAsync($"/api/Invoices/{invoice.Id}");
        var missing = await client.GetAsync($"/api/Invoices/{int.MaxValue}");

        await found.AssertStatusAsync(HttpStatusCode.OK);
        var json = await found.ReadJsonAsync();
        Assert.Equal(customer.Id, json.GetProperty("customer").GetProperty("id").GetInt32());
        await missing.AssertStatusAsync(HttpStatusCode.NotFound);
    }

    // Business ka faisla: Employee sab invoices dekh sakta hai (React Invoices page isi par chalta hai),
    // magar kisi aur ka draft badal nahi sakta (wo neeche ke tests me)
    [Fact]
    public async Task Employee_can_read_other_users_and_legacy_invoices()
    {
        var (_, employee) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var otherUser = await TestAuth.CreateUserAsync(_factory, Roles.Employee);
        var customer = await TestData.AddCustomerAsync(_factory);
        var othersInvoice = await TestData.AddInvoiceAsync(_factory, customer.Id, createdByUserId: otherUser.Id);
        var legacy = await TestData.AddInvoiceAsync(_factory, customer.Id, createdByUserId: null);

        var list = await employee.GetAsync("/api/Invoices");
        await list.AssertStatusAsync(HttpStatusCode.OK);
        var ids = (await list.ReadJsonAsync()).EnumerateArray()
            .Select(e => e.GetProperty("id").GetInt32())
            .ToList();
        Assert.Contains(othersInvoice.Id, ids);
        Assert.Contains(legacy.Id, ids);

        await (await employee.GetAsync($"/api/Invoices/{othersInvoice.Id}")).AssertStatusAsync(HttpStatusCode.OK);
        await (await employee.GetAsync($"/api/Invoices/{legacy.Id}")).AssertStatusAsync(HttpStatusCode.OK);
    }

    // ---------- Create ----------

    [Fact]
    public async Task Create_sets_creator_draft_status_total_and_upper_case_currency()
    {
        var (employee, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var customer = await TestData.AddCustomerAsync(_factory);

        var response = await client.PostAsJsonAsync("/api/Invoices",
            InvoicingApi.InvoicePayload(customer.Id, currency: "usd", subtotal: 1000m, taxAmount: 180m));

        await response.AssertStatusAsync(HttpStatusCode.Created);
        Assert.NotNull(response.Headers.Location);
        var json = await response.ReadJsonAsync();
        var id = json.GetProperty("id").GetInt32();
        Assert.Equal(employee.Id, json.GetProperty("createdByUserId").GetString());
        Assert.Equal(1180m, json.GetProperty("total").GetDecimal());
        Assert.Equal(InvoiceStatus.Draft, json.GetProperty("status").GetString());
        Assert.Equal("USD", json.GetProperty("currency").GetString());
        Assert.True(json.IsNull("submittedByUserId"));
        Assert.True(json.IsNull("reviewedByUserId"));

        var saved = await InvoicingApi.GetInvoiceAsync(_factory, id);
        Assert.Equal(employee.Id, saved.CreatedByUserId);
        Assert.Equal(1180m, saved.Total);
        Assert.Equal("USD", saved.Currency);
    }

    [Fact]
    public async Task Create_writes_InvoiceCreated_audit_row_for_the_creator()
    {
        var (employee, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var customer = await TestData.AddCustomerAsync(_factory);

        var id = await InvoicingApi.CreateInvoiceAsync(client, customer.Id);

        var rows = await InvoicingApi.AuditRowsAsync(_factory, "InvoiceCreated", "Invoice", id);
        var row = Assert.Single(rows);
        Assert.Equal(employee.Id, row.UserId);
        var invoice = await InvoicingApi.GetInvoiceAsync(_factory, id);
        Assert.Contains(invoice.InvoiceNumber, row.Details);
    }

    [Fact]
    public async Task Create_with_due_date_before_invoice_date_returns_400()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var customer = await TestData.AddCustomerAsync(_factory);
        var number = TestData.UniqueInvoiceNumber();
        var invoiceDate = new DateTime(2026, 10, 10);

        var response = await client.PostAsJsonAsync("/api/Invoices",
            InvoicingApi.InvoicePayload(customer.Id, number, invoiceDate: invoiceDate, dueDate: invoiceDate.AddDays(-1)));

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Contains("DueDate", await response.Content.ReadAsStringAsync());
        Assert.False(await InvoiceNumberExistsAsync(number));
    }

    [Fact]
    public async Task Create_with_unsupported_currency_returns_400()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var customer = await TestData.AddCustomerAsync(_factory);
        var number = TestData.UniqueInvoiceNumber();

        var response = await client.PostAsJsonAsync("/api/Invoices",
            InvoicingApi.InvoicePayload(customer.Id, number, currency: "JPY"));

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Contains("Currency", await response.Content.ReadAsStringAsync());
        Assert.False(await InvoiceNumberExistsAsync(number));
    }

    [Fact]
    public async Task Create_with_duplicate_number_returns_400()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var customer = await TestData.AddCustomerAsync(_factory);
        var number = TestData.UniqueInvoiceNumber();

        var first = await client.PostAsJsonAsync("/api/Invoices", InvoicingApi.InvoicePayload(customer.Id, number));
        var second = await client.PostAsJsonAsync("/api/Invoices", InvoicingApi.InvoicePayload(customer.Id, number));

        await first.AssertStatusAsync(HttpStatusCode.Created);
        await second.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Equal("Ye InvoiceNumber pehle se maujood hai.", await second.ReadMessageAsync());
        var count = await _factory.WithDbAsync(db => db.Invoices.CountAsync(i => i.InvoiceNumber == number));
        Assert.Equal(1, count);
    }

    // decimal(18,2): 10.005 + 0.005 chupke se 10.01 + 0.01 ho jata aur Total (10.01) us se match na karta
    [Theory]
    [InlineData(10.005, 0.0)]
    [InlineData(10.0, 0.005)]
    public async Task Create_with_more_than_two_decimals_returns_400(double subtotal, double taxAmount)
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var customer = await TestData.AddCustomerAsync(_factory);
        var number = TestData.UniqueInvoiceNumber();

        var response = await client.PostAsJsonAsync("/api/Invoices",
            InvoicingApi.InvoicePayload(customer.Id, number, subtotal: (decimal)subtotal, taxAmount: (decimal)taxAmount));

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Contains("2 se zyada decimal", await response.Content.ReadAsStringAsync());
        Assert.False(await InvoiceNumberExistsAsync(number));
    }

    [Fact]
    public async Task Update_with_more_than_two_decimals_returns_400_and_keeps_amounts()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var customer = await TestData.AddCustomerAsync(_factory);
        var id = await InvoicingApi.CreateInvoiceAsync(client, customer.Id);
        var before = await InvoicingApi.GetInvoiceAsync(_factory, id);

        var response = await client.PutAsJsonAsync($"/api/Invoices/{id}",
            InvoicingApi.InvoicePayload(customer.Id, before.InvoiceNumber, subtotal: 99.999m, taxAmount: 0m));

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        var after = await InvoicingApi.GetInvoiceAsync(_factory, id);
        Assert.Equal(before.Subtotal, after.Subtotal);
        Assert.Equal(before.Total, after.Total);
        Assert.Empty(await InvoicingApi.AuditRowsAsync(_factory, "InvoiceUpdated", "Invoice", id));
    }

    [Fact]
    public async Task Create_with_unknown_customer_returns_400()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var number = TestData.UniqueInvoiceNumber();

        var response = await client.PostAsJsonAsync("/api/Invoices", InvoicingApi.InvoicePayload(int.MaxValue, number));

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Equal("Is CustomerId ka customer maujood nahi.", await response.ReadMessageAsync());
        Assert.False(await InvoiceNumberExistsAsync(number));
    }

    // ---------- Update ----------

    [Fact]
    public async Task Creator_can_update_own_draft_and_total_is_recomputed()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var customer = await TestData.AddCustomerAsync(_factory);
        var id = await InvoicingApi.CreateInvoiceAsync(client, customer.Id);
        var newNumber = TestData.UniqueInvoiceNumber();

        var response = await client.PutAsJsonAsync($"/api/Invoices/{id}",
            InvoicingApi.InvoicePayload(customer.Id, newNumber, currency: "eur", subtotal: 2000m, taxAmount: 100m));

        await response.AssertStatusAsync(HttpStatusCode.NoContent);
        var saved = await InvoicingApi.GetInvoiceAsync(_factory, id);
        Assert.Equal(newNumber, saved.InvoiceNumber);
        Assert.Equal(2100m, saved.Total);
        Assert.Equal("EUR", saved.Currency);
        Assert.Equal(InvoiceStatus.Draft, saved.Status);
        Assert.Single(await InvoicingApi.AuditRowsAsync(_factory, "InvoiceUpdated", "Invoice", id));
    }

    [Fact]
    public async Task Employee_cannot_update_another_users_draft()
    {
        var (_, owner) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var (_, other) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var customer = await TestData.AddCustomerAsync(_factory);
        var id = await InvoicingApi.CreateInvoiceAsync(owner, customer.Id);
        var before = await InvoicingApi.GetInvoiceAsync(_factory, id);

        var response = await other.PutAsJsonAsync($"/api/Invoices/{id}",
            InvoicingApi.InvoicePayload(customer.Id, subtotal: 5m, taxAmount: 0m));

        await response.AssertStatusAsync(HttpStatusCode.Forbidden);
        var after = await InvoicingApi.GetInvoiceAsync(_factory, id);
        Assert.Equal(before.InvoiceNumber, after.InvoiceNumber);
        Assert.Equal(before.Total, after.Total);
    }

    [Fact]
    public async Task Manager_can_update_an_employees_draft()
    {
        var (_, employee) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var (_, manager) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var customer = await TestData.AddCustomerAsync(_factory);
        var id = await InvoicingApi.CreateInvoiceAsync(employee, customer.Id);

        var response = await manager.PutAsJsonAsync($"/api/Invoices/{id}",
            InvoicingApi.InvoicePayload(customer.Id, subtotal: 300m, taxAmount: 0m));

        await response.AssertStatusAsync(HttpStatusCode.NoContent);
        Assert.Equal(300m, (await InvoicingApi.GetInvoiceAsync(_factory, id)).Total);
    }

    [Fact]
    public async Task Legacy_invoice_without_creator_is_editable_only_by_managers()
    {
        var (_, employee) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var (_, manager) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var customer = await TestData.AddCustomerAsync(_factory);
        var legacy = await TestData.AddInvoiceAsync(_factory, customer.Id, createdByUserId: null);

        var byEmployee = await employee.PutAsJsonAsync($"/api/Invoices/{legacy.Id}",
            InvoicingApi.InvoicePayload(customer.Id, legacy.InvoiceNumber, subtotal: 10m, taxAmount: 0m));
        var byManager = await manager.PutAsJsonAsync($"/api/Invoices/{legacy.Id}",
            InvoicingApi.InvoicePayload(customer.Id, legacy.InvoiceNumber, subtotal: 20m, taxAmount: 0m));

        await byEmployee.AssertStatusAsync(HttpStatusCode.Forbidden);
        await byManager.AssertStatusAsync(HttpStatusCode.NoContent);
        Assert.Equal(20m, (await InvoicingApi.GetInvoiceAsync(_factory, legacy.Id)).Total);
    }

    [Fact]
    public async Task Update_of_non_draft_invoice_returns_400()
    {
        var (_, manager) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var customer = await TestData.AddCustomerAsync(_factory);
        var invoice = await TestData.AddInvoiceAsync(_factory, customer.Id, status: InvoiceStatus.Approved);

        var response = await manager.PutAsJsonAsync($"/api/Invoices/{invoice.Id}",
            InvoicingApi.InvoicePayload(customer.Id, invoice.InvoiceNumber, subtotal: 1m, taxAmount: 0m));

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Equal("Sirf Draft invoice edit ho sakta hai.", await response.ReadMessageAsync());
        Assert.Equal(invoice.Total, (await InvoicingApi.GetInvoiceAsync(_factory, invoice.Id)).Total);
    }

    [Fact]
    public async Task Update_to_a_number_used_by_another_invoice_returns_400()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var customer = await TestData.AddCustomerAsync(_factory);
        var other = await TestData.AddInvoiceAsync(_factory, customer.Id);
        var id = await InvoicingApi.CreateInvoiceAsync(client, customer.Id);

        var response = await client.PutAsJsonAsync($"/api/Invoices/{id}",
            InvoicingApi.InvoicePayload(customer.Id, other.InvoiceNumber));

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Equal("Ye InvoiceNumber pehle se maujood hai.", await response.ReadMessageAsync());
    }

    [Fact]
    public async Task Update_keeping_its_own_number_is_allowed()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var customer = await TestData.AddCustomerAsync(_factory);
        var id = await InvoicingApi.CreateInvoiceAsync(client, customer.Id);
        var number = (await InvoicingApi.GetInvoiceAsync(_factory, id)).InvoiceNumber;

        var response = await client.PutAsJsonAsync($"/api/Invoices/{id}",
            InvoicingApi.InvoicePayload(customer.Id, number, subtotal: 50m, taxAmount: 5m));

        await response.AssertStatusAsync(HttpStatusCode.NoContent);
        Assert.Equal(55m, (await InvoicingApi.GetInvoiceAsync(_factory, id)).Total);
    }

    [Fact]
    public async Task Update_with_unknown_customer_returns_400()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var customer = await TestData.AddCustomerAsync(_factory);
        var id = await InvoicingApi.CreateInvoiceAsync(client, customer.Id);

        var response = await client.PutAsJsonAsync($"/api/Invoices/{id}",
            InvoicingApi.InvoicePayload(int.MaxValue));

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Equal("Is CustomerId ka customer maujood nahi.", await response.ReadMessageAsync());
    }

    [Fact]
    public async Task Update_of_unknown_invoice_returns_404()
    {
        var (_, manager) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var customer = await TestData.AddCustomerAsync(_factory);

        var response = await manager.PutAsJsonAsync($"/api/Invoices/{int.MaxValue}",
            InvoicingApi.InvoicePayload(customer.Id));

        await response.AssertStatusAsync(HttpStatusCode.NotFound);
    }

    // ---------- Submit ----------

    [Fact]
    public async Task Employee_creates_and_submits_own_draft()
    {
        var (employee, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var customer = await TestData.AddCustomerAsync(_factory);
        var id = await InvoicingApi.CreateInvoiceAsync(client, customer.Id);

        var response = await InvoicingApi.PostWithoutBodyAsync(client, $"/api/Invoices/{id}/submit");

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var json = await response.ReadJsonAsync();
        Assert.Equal(InvoiceStatus.PendingApproval, json.GetProperty("status").GetString());
        Assert.Equal(employee.Id, json.GetProperty("submittedByUserId").GetString());

        var saved = await InvoicingApi.GetInvoiceAsync(_factory, id);
        Assert.Equal(InvoiceStatus.PendingApproval, saved.Status);
        Assert.Equal(employee.Id, saved.SubmittedByUserId);
        var audit = Assert.Single(await InvoicingApi.AuditRowsAsync(_factory, "InvoiceSubmitted", "Invoice", id));
        Assert.Equal(employee.Id, audit.UserId);
    }

    [Fact]
    public async Task Employee_cannot_submit_another_users_draft()
    {
        var (_, owner) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var (_, other) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var customer = await TestData.AddCustomerAsync(_factory);
        var id = await InvoicingApi.CreateInvoiceAsync(owner, customer.Id);

        var response = await InvoicingApi.PostWithoutBodyAsync(other, $"/api/Invoices/{id}/submit");

        await response.AssertStatusAsync(HttpStatusCode.Forbidden);
        var saved = await InvoicingApi.GetInvoiceAsync(_factory, id);
        Assert.Equal(InvoiceStatus.Draft, saved.Status);
        Assert.Null(saved.SubmittedByUserId);
    }

    [Fact]
    public async Task Legacy_invoice_can_be_submitted_only_by_managers()
    {
        var (_, employee) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var (manager, managerClient) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var customer = await TestData.AddCustomerAsync(_factory);
        var legacy = await TestData.AddInvoiceAsync(_factory, customer.Id, createdByUserId: null);

        var byEmployee = await InvoicingApi.PostWithoutBodyAsync(employee, $"/api/Invoices/{legacy.Id}/submit");
        var byManager = await InvoicingApi.PostWithoutBodyAsync(managerClient, $"/api/Invoices/{legacy.Id}/submit");

        await byEmployee.AssertStatusAsync(HttpStatusCode.Forbidden);
        await byManager.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Equal(manager.Id, (await InvoicingApi.GetInvoiceAsync(_factory, legacy.Id)).SubmittedByUserId);
    }

    [Fact]
    public async Task Submit_of_non_draft_invoice_returns_400()
    {
        var (_, manager) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var customer = await TestData.AddCustomerAsync(_factory);
        var invoice = await TestData.AddInvoiceAsync(_factory, customer.Id, status: InvoiceStatus.PendingApproval);

        var response = await InvoicingApi.PostWithoutBodyAsync(manager, $"/api/Invoices/{invoice.Id}/submit");

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Equal("Sirf Draft invoice submit ho sakta hai.", await response.ReadMessageAsync());
    }

    // ---------- Delete ----------

    [Fact]
    public async Task Employee_cannot_delete_even_own_draft()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var customer = await TestData.AddCustomerAsync(_factory);
        var id = await InvoicingApi.CreateInvoiceAsync(client, customer.Id);

        var response = await client.DeleteAsync($"/api/Invoices/{id}");

        await response.AssertStatusAsync(HttpStatusCode.Forbidden);
        Assert.NotNull(await InvoicingApi.FindInvoiceAsync(_factory, id));
    }

    [Fact]
    public async Task Manager_deletes_draft_and_audit_row_is_written()
    {
        var (manager, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var customer = await TestData.AddCustomerAsync(_factory);
        var invoice = await TestData.AddInvoiceAsync(_factory, customer.Id);

        var response = await client.DeleteAsync($"/api/Invoices/{invoice.Id}");

        await response.AssertStatusAsync(HttpStatusCode.NoContent);
        Assert.Null(await InvoicingApi.FindInvoiceAsync(_factory, invoice.Id));
        var audit = Assert.Single(await InvoicingApi.AuditRowsAsync(_factory, "InvoiceDeleted", "Invoice", invoice.Id));
        Assert.Equal(manager.Id, audit.UserId);
        Assert.Contains(invoice.InvoiceNumber, audit.Details);
    }

    [Theory]
    [InlineData(InvoiceStatus.PendingApproval)]
    [InlineData(InvoiceStatus.Approved)]
    [InlineData(InvoiceStatus.Paid)]
    [InlineData(InvoiceStatus.Cancelled)]
    public async Task Delete_of_non_draft_invoice_returns_400(string status)
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var customer = await TestData.AddCustomerAsync(_factory);
        var invoice = await TestData.AddInvoiceAsync(_factory, customer.Id, status: status);

        var response = await client.DeleteAsync($"/api/Invoices/{invoice.Id}");

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Equal("Sirf Draft invoice delete ho sakta hai.", await response.ReadMessageAsync());
        Assert.NotNull(await InvoicingApi.FindInvoiceAsync(_factory, invoice.Id));
    }

    [Fact]
    public async Task Delete_of_unknown_invoice_returns_404()
    {
        var client = await TestAuth.AdminClientAsync(_factory);

        var response = await client.DeleteAsync($"/api/Invoices/{int.MaxValue}");

        await response.AssertStatusAsync(HttpStatusCode.NotFound);
    }
}
