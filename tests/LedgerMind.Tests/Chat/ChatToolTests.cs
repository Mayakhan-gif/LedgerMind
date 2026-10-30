using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using LedgerMind.Models;
using LedgerMind.Tests.Infrastructure;
using TaskStatus = LedgerMind.Models.TaskStatus;

namespace LedgerMind.Tests.Chat;

// Chatbot tools: role checks aur data ke qaide (Groq ko gaye "tool" message se parhe jate hain)
public class ChatToolTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public ChatToolTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _factory.GroqHttp.Reset();
    }

    // Model pehle ek tool mangta hai, phir "done" kehta hai. Wapis: tool ka JSON result.
    private Task<JsonElement> RunToolAsync(HttpClient client, string toolName, string arguments = "{}") =>
        RunToolAsync(client, GroqFake.Call("call_1", toolName, arguments));

    private async Task<JsonElement> RunToolAsync(HttpClient client, JsonObject call)
    {
        _factory.GroqHttp.Reset();
        _factory.GroqHttp.EnqueueJson(HttpStatusCode.OK, GroqFake.ToolCallsResponse(call));
        _factory.GroqHttp.EnqueueJson(HttpStatusCode.OK, GroqFake.TextResponse("done"));

        var response = await client.PostAsJsonAsync("/api/Chat", new { message = "please check" });

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var requests = _factory.GroqHttp.Requests.ToArray();
        Assert.Equal(2, requests.Length);

        var toolMessage = Assert.Single(GroqFake.ToolMessages(requests[1]));
        return GroqFake.ToolContent(toolMessage);
    }

    private static string? Error(JsonElement content) =>
        content.TryGetProperty("error", out var error) ? error.GetString() : null;

    private static List<string> InvoiceNumbers(JsonElement content) =>
        content.GetProperty("invoices").EnumerateArray()
            .Select(i => i.GetProperty("invoiceNumber").GetString() ?? string.Empty)
            .ToList();

    [Fact]
    public async Task Employee_gets_error_object_from_overdue_invoices_tool()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);

        var content = await RunToolAsync(client, "get_overdue_invoices");

        Assert.False(string.IsNullOrWhiteSpace(Error(content)));
        Assert.False(content.TryGetProperty("invoices", out _));
    }

    [Fact]
    public async Task Employee_gets_error_object_from_pending_approvals_tool()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);

        var content = await RunToolAsync(client, "get_pending_approvals");

        Assert.False(string.IsNullOrWhiteSpace(Error(content)));
        Assert.False(content.TryGetProperty("invoices", out _));
        Assert.False(content.TryGetProperty("expenses", out _));
    }

    [Fact]
    public async Task Manager_overdue_tool_lists_only_overdue_invoices_with_balance()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var customer = await TestData.AddCustomerAsync(_factory);
        var longAgo = DateTime.UtcNow.Date.AddYears(-10);

        var overdue = await TestData.AddInvoiceAsync(_factory, customer.Id, InvoiceStatus.Approved,
            subtotal: 1000m, invoiceDate: longAgo.AddDays(-30), dueDate: longAgo);
        await TestData.AddPaymentAsync(_factory, overdue.Id, 250m);

        var notDueYet = await TestData.AddInvoiceAsync(_factory, customer.Id, InvoiceStatus.Approved,
            dueDate: DateTime.UtcNow.Date.AddDays(10));
        var excluded = await TestData.AddInvoiceAsync(_factory, customer.Id, InvoiceStatus.Approved,
            invoiceDate: longAgo.AddDays(-30), dueDate: longAgo, isExcluded: true);
        var paid = await TestData.AddInvoiceAsync(_factory, customer.Id, InvoiceStatus.Paid,
            invoiceDate: longAgo.AddDays(-30), dueDate: longAgo);
        var draft = await TestData.AddInvoiceAsync(_factory, customer.Id, InvoiceStatus.Draft,
            invoiceDate: longAgo.AddDays(-30), dueDate: longAgo);

        var content = await RunToolAsync(client, "get_overdue_invoices");

        Assert.Null(Error(content));
        Assert.True(content.GetProperty("totalCount").GetInt32() >= 1);

        var rows = content.GetProperty("invoices").EnumerateArray().ToList();
        var row = Assert.Single(rows, r => r.GetProperty("invoiceNumber").GetString() == overdue.InvoiceNumber);
        Assert.Equal(customer.Name, row.GetProperty("customer").GetString());
        Assert.Equal("PKR", row.GetProperty("currency").GetString());
        Assert.Equal(1000m, row.GetProperty("total").GetDecimal());
        Assert.Equal(250m, row.GetProperty("paid").GetDecimal());
        Assert.Equal(750m, row.GetProperty("balance").GetDecimal());
        Assert.Equal(longAgo.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), row.GetProperty("dueDate").GetString());
        Assert.Equal(InvoiceStatus.Approved, row.GetProperty("status").GetString());

        var numbers = InvoiceNumbers(content);
        Assert.DoesNotContain(notDueYet.InvoiceNumber, numbers);
        Assert.DoesNotContain(excluded.InvoiceNumber, numbers);
        Assert.DoesNotContain(paid.InvoiceNumber, numbers);
        Assert.DoesNotContain(draft.InvoiceNumber, numbers);
    }

    [Fact]
    public async Task Manager_pending_approvals_tool_hides_own_invoices_and_expenses()
    {
        var (manager, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var other = await TestAuth.CreateUserAsync(_factory, Roles.Employee);
        var customer = await TestData.AddCustomerAsync(_factory);
        var old = DateTime.UtcNow.Date.AddYears(-20);

        var createdByMe = await TestData.AddInvoiceAsync(_factory, customer.Id, InvoiceStatus.PendingApproval,
            invoiceDate: old, dueDate: old.AddDays(30), createdByUserId: manager.Id, submittedByUserId: other.Id);
        var submittedByMe = await TestData.AddInvoiceAsync(_factory, customer.Id, InvoiceStatus.PendingApproval,
            invoiceDate: old, dueDate: old.AddDays(30), createdByUserId: other.Id, submittedByUserId: manager.Id);
        var someoneElses = await TestData.AddInvoiceAsync(_factory, customer.Id, InvoiceStatus.PendingApproval,
            invoiceDate: old, dueDate: old.AddDays(30), createdByUserId: other.Id, submittedByUserId: other.Id);
        var legacy = await TestData.AddInvoiceAsync(_factory, customer.Id, InvoiceStatus.PendingApproval,
            invoiceDate: old, dueDate: old.AddDays(30));
        var draft = await TestData.AddInvoiceAsync(_factory, customer.Id, InvoiceStatus.Draft,
            invoiceDate: old, dueDate: old.AddDays(30), createdByUserId: other.Id);

        var myExpense = await TestData.AddExpenseAsync(_factory, manager.Id, ExpenseStatus.Submitted, expenseDate: old);
        var otherExpense = await TestData.AddExpenseAsync(_factory, other.Id, ExpenseStatus.Submitted, expenseDate: old);
        var otherDraft = await TestData.AddExpenseAsync(_factory, other.Id, ExpenseStatus.Draft, expenseDate: old);

        var content = await RunToolAsync(client, "get_pending_approvals");

        Assert.Null(Error(content));
        var numbers = InvoiceNumbers(content);
        Assert.Contains(someoneElses.InvoiceNumber, numbers);
        Assert.Contains(legacy.InvoiceNumber, numbers);
        Assert.DoesNotContain(createdByMe.InvoiceNumber, numbers);
        Assert.DoesNotContain(submittedByMe.InvoiceNumber, numbers);
        Assert.DoesNotContain(draft.InvoiceNumber, numbers);

        var expenseIds = content.GetProperty("expenses").EnumerateArray()
            .Select(e => e.GetProperty("id").GetInt32())
            .ToList();
        Assert.Contains(otherExpense.Id, expenseIds);
        Assert.DoesNotContain(myExpense.Id, expenseIds);
        Assert.DoesNotContain(otherDraft.Id, expenseIds);

        Assert.True(content.GetProperty("invoiceCount").GetInt32() >= 2);
        Assert.True(content.GetProperty("expenseCount").GetInt32() >= 1);
    }

    // Manager ne kisi aur ka Draft edit kiya: approvals queue ki tarah tool bhi usay us manager ko nahi dikhata
    [Fact]
    public async Task Manager_pending_approvals_tool_hides_invoices_the_manager_edited()
    {
        var (_, employeeClient) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var (_, editorClient) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var (_, colleagueClient) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var customer = await TestData.AddCustomerAsync(_factory);

        // Bohot purani tareekh: tool ki 20 rows (purani pehle) me ye zaroor aaye
        var veryOld = DateTime.UtcNow.Date.AddYears(-40);
        var number = TestData.UniqueInvoiceNumber();
        var payload = new
        {
            invoiceNumber = number,
            customerId = customer.Id,
            description = "Edited by manager",
            invoiceDate = veryOld,
            dueDate = veryOld.AddDays(30),
            currency = "PKR",
            subtotal = 1000m,
            taxAmount = 0m,
            isExcluded = false
        };

        var create = await employeeClient.PostAsJsonAsync("/api/Invoices", payload);
        await create.AssertStatusAsync(HttpStatusCode.Created);
        var id = (await create.ReadJsonAsync()).GetProperty("id").GetInt32();
        var edit = await editorClient.PutAsJsonAsync($"/api/Invoices/{id}", payload);
        await edit.AssertStatusAsync(HttpStatusCode.NoContent);
        var submit = await employeeClient.PostAsync($"/api/Invoices/{id}/submit", null);
        await submit.AssertStatusAsync(HttpStatusCode.OK);

        var editorContent = await RunToolAsync(editorClient, "get_pending_approvals");
        var colleagueContent = await RunToolAsync(colleagueClient, "get_pending_approvals");

        Assert.Null(Error(editorContent));
        Assert.DoesNotContain(number, InvoiceNumbers(editorContent));
        Assert.Null(Error(colleagueContent));
        Assert.Contains(number, InvoiceNumbers(colleagueContent));
    }

    [Fact]
    public async Task My_tasks_tool_returns_only_open_tasks_assigned_to_caller()
    {
        var (user, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var other = await TestAuth.CreateUserAsync(_factory, Roles.Employee);
        var dueTomorrow = DateTime.UtcNow.Date.AddDays(1);

        var open = await TestData.AddTaskAsync(_factory, user.Id, other.Id, dueDate: dueTomorrow);
        var noDate = await TestData.AddTaskAsync(_factory, user.Id, other.Id);
        var done = await TestData.AddTaskAsync(_factory, user.Id, other.Id, status: TaskStatus.Done);
        var notMine = await TestData.AddTaskAsync(_factory, other.Id, user.Id);

        var content = await RunToolAsync(client, "get_my_tasks");

        var titles = content.GetProperty("tasks").EnumerateArray()
            .Select(t => t.GetProperty("title").GetString() ?? string.Empty)
            .ToList();
        Assert.Equal(new[] { open.Title, noDate.Title }, titles);
        Assert.DoesNotContain(done.Title, titles);
        Assert.DoesNotContain(notMine.Title, titles);
        Assert.Equal(2, content.GetProperty("count").GetInt32());

        var first = content.GetProperty("tasks")[0];
        Assert.Equal(dueTomorrow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), first.GetProperty("dueDate").GetString());
    }

    [Fact]
    public async Task Expense_summary_for_employee_counts_only_own_expenses_in_that_month()
    {
        var (employee, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var other = await TestAuth.CreateUserAsync(_factory, Roles.Employee);
        var category = $"Cat-{Guid.NewGuid():N}"[..16];
        var march = new DateTime(2025, 3, 10);

        await TestData.AddExpenseAsync(_factory, employee.Id, ExpenseStatus.Submitted, 100m, "PKR", category, march);
        await TestData.AddExpenseAsync(_factory, employee.Id, ExpenseStatus.Approved, 50m, "PKR", category, march.AddDays(21));
        await TestData.AddExpenseAsync(_factory, employee.Id, ExpenseStatus.Draft, 999m, "PKR", category, march);
        await TestData.AddExpenseAsync(_factory, employee.Id, ExpenseStatus.Rejected, 888m, "PKR", category, march);
        await TestData.AddExpenseAsync(_factory, employee.Id, ExpenseStatus.Submitted, 300m, "PKR", category, new DateTime(2025, 4, 1));
        await TestData.AddExpenseAsync(_factory, other.Id, ExpenseStatus.Submitted, 70m, "PKR", category, march);

        var content = await RunToolAsync(client, "get_expense_summary", "{\"year\":2025,\"month\":3}");

        Assert.Null(Error(content));
        Assert.Equal(2025, content.GetProperty("year").GetInt32());
        Assert.Equal(3, content.GetProperty("month").GetInt32());
        Assert.Equal("mine", content.GetProperty("scope").GetString());

        var groups = content.GetProperty("groups").EnumerateArray().ToList();
        var group = Assert.Single(groups, g => g.GetProperty("category").GetString() == category);
        Assert.Equal("PKR", group.GetProperty("currency").GetString());
        Assert.Equal(2, group.GetProperty("count").GetInt32());
        Assert.Equal(150m, group.GetProperty("total").GetDecimal());
    }

    [Fact]
    public async Task Expense_summary_for_manager_includes_everyone_and_keeps_currencies_apart()
    {
        var (manager, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var other = await TestAuth.CreateUserAsync(_factory, Roles.Employee);
        var category = $"Cat-{Guid.NewGuid():N}"[..16];
        var june = new DateTime(2024, 6, 5);

        await TestData.AddExpenseAsync(_factory, manager.Id, ExpenseStatus.Submitted, 100m, "PKR", category, june);
        await TestData.AddExpenseAsync(_factory, other.Id, ExpenseStatus.Approved, 40m, "PKR", category, june);
        await TestData.AddExpenseAsync(_factory, other.Id, ExpenseStatus.Submitted, 25m, "USD", category, june);

        var content = await RunToolAsync(client, "get_expense_summary", "{\"year\":2024,\"month\":6}");

        Assert.Equal("all", content.GetProperty("scope").GetString());
        var groups = content.GetProperty("groups").EnumerateArray()
            .Where(g => g.GetProperty("category").GetString() == category)
            .ToList();
        Assert.Equal(2, groups.Count);

        var pkr = Assert.Single(groups, g => g.GetProperty("currency").GetString() == "PKR");
        Assert.Equal(2, pkr.GetProperty("count").GetInt32());
        Assert.Equal(140m, pkr.GetProperty("total").GetDecimal());

        var usd = Assert.Single(groups, g => g.GetProperty("currency").GetString() == "USD");
        Assert.Equal(25m, usd.GetProperty("total").GetDecimal());
    }

    [Fact]
    public async Task Expense_summary_accepts_numbers_sent_as_strings()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);

        var content = await RunToolAsync(client, "get_expense_summary", "{\"year\":\"2023\",\"month\":\"11\"}");

        Assert.Null(Error(content));
        Assert.Equal(2023, content.GetProperty("year").GetInt32());
        Assert.Equal(11, content.GetProperty("month").GetInt32());
    }

    [Fact]
    public async Task Expense_summary_accepts_arguments_sent_as_json_object()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var arguments = new JsonObject { ["year"] = 2022, ["month"] = 2 };

        var content = await RunToolAsync(client, GroqFake.Call("call_obj", "get_expense_summary", arguments));

        Assert.Null(Error(content));
        Assert.Equal(2022, content.GetProperty("year").GetInt32());
        Assert.Equal(2, content.GetProperty("month").GetInt32());
    }

    [Fact]
    public async Task Expense_summary_without_arguments_uses_current_month()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var before = DateTime.UtcNow;

        var content = await RunToolAsync(client, "get_expense_summary", "");

        var after = DateTime.UtcNow;
        var year = content.GetProperty("year").GetInt32();
        var month = content.GetProperty("month").GetInt32();
        Assert.True(
            (year == before.Year && month == before.Month) || (year == after.Year && month == after.Month),
            $"Unexpected period {year}-{month}");
    }

    [Theory]
    [InlineData("{\"year\":2025,\"month\":13}")]
    [InlineData("{\"year\":2025,\"month\":0}")]
    [InlineData("{\"year\":1999,\"month\":5}")]
    [InlineData("{\"year\":2025,\"month\":\"march\"}")]
    [InlineData("{not json")]
    public async Task Expense_summary_with_bad_arguments_returns_error_object(string arguments)
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);

        var content = await RunToolAsync(client, "get_expense_summary", arguments);

        Assert.False(string.IsNullOrWhiteSpace(Error(content)));
        Assert.False(content.TryGetProperty("groups", out _));
    }

    [Fact]
    public async Task Unknown_tool_returns_error_object()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Admin);

        var content = await RunToolAsync(client, "delete_all_invoices");

        Assert.False(string.IsNullOrWhiteSpace(Error(content)));
    }
}
