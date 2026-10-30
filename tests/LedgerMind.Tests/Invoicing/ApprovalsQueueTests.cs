using System.Net;
using System.Net.Http.Json;
using LedgerMind.Models;
using LedgerMind.Tests.Infrastructure;

namespace LedgerMind.Tests.Invoicing;

// GET /api/Approvals/pending: segregation of duties wali queue
public class ApprovalsQueueTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public ApprovalsQueueTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Queue_hides_callers_own_invoices_and_expenses_but_shows_others()
    {
        var (manager, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var employee = await TestAuth.CreateUserAsync(_factory, Roles.Employee);
        var customer = await TestData.AddCustomerAsync(_factory);

        var createdByMe = await TestData.AddInvoiceAsync(_factory, customer.Id,
            status: InvoiceStatus.PendingApproval, createdByUserId: manager.Id, submittedByUserId: employee.Id);
        var submittedByMe = await TestData.AddInvoiceAsync(_factory, customer.Id,
            status: InvoiceStatus.PendingApproval, createdByUserId: employee.Id, submittedByUserId: manager.Id);
        var others = await TestData.AddInvoiceAsync(_factory, customer.Id,
            status: InvoiceStatus.PendingApproval, createdByUserId: employee.Id, submittedByUserId: employee.Id);
        var legacy = await TestData.AddInvoiceAsync(_factory, customer.Id, status: InvoiceStatus.PendingApproval);
        var draft = await TestData.AddInvoiceAsync(_factory, customer.Id,
            status: InvoiceStatus.Draft, createdByUserId: employee.Id);

        var myExpense = await TestData.AddExpenseAsync(_factory, manager.Id, ExpenseStatus.Submitted);
        var theirExpense = await TestData.AddExpenseAsync(_factory, employee.Id, ExpenseStatus.Submitted);
        var theirDraftExpense = await TestData.AddExpenseAsync(_factory, employee.Id, ExpenseStatus.Draft);

        var response = await client.GetAsync("/api/Approvals/pending");

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var json = await response.ReadJsonAsync();
        var invoiceIds = json.Ids("invoices");
        var expenseIds = json.Ids("expenses");

        Assert.Contains(others.Id, invoiceIds);
        Assert.Contains(legacy.Id, invoiceIds);
        Assert.DoesNotContain(createdByMe.Id, invoiceIds);
        Assert.DoesNotContain(submittedByMe.Id, invoiceIds);
        Assert.DoesNotContain(draft.Id, invoiceIds);

        Assert.Contains(theirExpense.Id, expenseIds);
        Assert.DoesNotContain(myExpense.Id, expenseIds);
        Assert.DoesNotContain(theirDraftExpense.Id, expenseIds);

        Assert.Equal(invoiceIds.Count, json.GetProperty("invoiceCount").GetInt32());
        Assert.Equal(expenseIds.Count, json.GetProperty("expenseCount").GetInt32());
    }

    [Fact]
    public async Task Queue_rows_keep_the_existing_shape()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var employee = await TestAuth.CreateUserAsync(_factory, Roles.Employee);
        var customer = await TestData.AddCustomerAsync(_factory);
        var invoice = await TestData.AddInvoiceAsync(_factory, customer.Id,
            status: InvoiceStatus.PendingApproval, subtotal: 400m, taxAmount: 40m, currency: "USD",
            createdByUserId: employee.Id, submittedByUserId: employee.Id);

        var response = await client.GetAsync("/api/Approvals/pending");

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var json = await response.ReadJsonAsync();
        var row = json.GetProperty("invoices").EnumerateArray()
            .Single(e => e.GetProperty("id").GetInt32() == invoice.Id);
        Assert.Equal(invoice.InvoiceNumber, row.GetProperty("invoiceNumber").GetString());
        Assert.Equal(customer.Name, row.GetProperty("customer").GetString());
        Assert.Equal("USD", row.GetProperty("currency").GetString());
        Assert.Equal(440m, row.GetProperty("total").GetDecimal());
        Assert.True(row.TryGetProperty("dueDate", out _));
    }

    [Fact]
    public async Task Invoice_created_by_one_manager_appears_in_another_managers_queue()
    {
        var (author, authorClient) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var (_, colleagueClient) = await TestAuth.NewUserClientAsync(_factory, Roles.Admin);
        var customer = await TestData.AddCustomerAsync(_factory);
        var invoice = await TestData.AddInvoiceAsync(_factory, customer.Id,
            status: InvoiceStatus.PendingApproval, createdByUserId: author.Id, submittedByUserId: author.Id);

        var authorQueue = await authorClient.GetAsync("/api/Approvals/pending");
        var colleagueQueue = await colleagueClient.GetAsync("/api/Approvals/pending");

        await authorQueue.AssertStatusAsync(HttpStatusCode.OK);
        await colleagueQueue.AssertStatusAsync(HttpStatusCode.OK);
        Assert.DoesNotContain(invoice.Id, (await authorQueue.ReadJsonAsync()).Ids("invoices"));
        Assert.Contains(invoice.Id, (await colleagueQueue.ReadJsonAsync()).Ids("invoices"));
    }

    [Fact]
    public async Task Invoice_edited_by_a_manager_is_hidden_from_that_managers_queue_only()
    {
        var (_, employeeClient) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var (_, editorClient) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var (_, colleagueClient) = await TestAuth.NewUserClientAsync(_factory, Roles.Admin);
        var customer = await TestData.AddCustomerAsync(_factory);
        var id = await InvoicingApi.CreateInvoiceAsync(employeeClient, customer.Id);
        var number = (await InvoicingApi.GetInvoiceAsync(_factory, id)).InvoiceNumber;

        var edit = await editorClient.PutAsJsonAsync($"/api/Invoices/{id}",
            InvoicingApi.InvoicePayload(customer.Id, number, subtotal: 5000m));
        await edit.AssertStatusAsync(HttpStatusCode.NoContent);
        await (await InvoicingApi.PostWithoutBodyAsync(employeeClient, $"/api/Invoices/{id}/submit"))
            .AssertStatusAsync(HttpStatusCode.OK);

        var editorQueue = await editorClient.GetAsync("/api/Approvals/pending");
        var colleagueQueue = await colleagueClient.GetAsync("/api/Approvals/pending");

        await editorQueue.AssertStatusAsync(HttpStatusCode.OK);
        await colleagueQueue.AssertStatusAsync(HttpStatusCode.OK);
        var editorJson = await editorQueue.ReadJsonAsync();
        var editorIds = editorJson.Ids("invoices");
        Assert.DoesNotContain(id, editorIds);
        Assert.Equal(editorIds.Count, editorJson.GetProperty("invoiceCount").GetInt32());
        Assert.Contains(id, (await colleagueQueue.ReadJsonAsync()).Ids("invoices"));
    }

    [Fact]
    public async Task Employee_gets_403()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);

        var response = await client.GetAsync("/api/Approvals/pending");

        await response.AssertStatusAsync(HttpStatusCode.Forbidden);
    }
}
