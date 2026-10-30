using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LedgerMind.Models;
using LedgerMind.Tests.Infrastructure;

namespace LedgerMind.Tests.Expenses;

// Approve / reject: sirf manager, apna nahi (segregation of duties), sirf Submitted, audit ke saath
public class ExpenseReviewTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public ExpenseReviewTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private async Task<Expense> SubmittedExpenseOfNewEmployeeAsync()
    {
        var employee = await TestAuth.CreateUserAsync(_factory, Roles.Employee);
        return await TestData.AddExpenseAsync(_factory, employee.Id, ExpenseStatus.Submitted);
    }

    [Fact]
    public async Task Manager_approves_someone_elses_expense_and_comment_and_audit_are_stored()
    {
        var expense = await SubmittedExpenseOfNewEmployeeAsync();
        var (manager, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);

        var response = await client.PostAsJsonAsync(
            $"/api/Expenses/{expense.Id}/approve", new { comment = "Receipt theek hai" });

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var json = await response.ReadJsonAsync();
        Assert.Equal(ExpenseStatus.Approved, json.GetProperty("status").GetString());
        Assert.Equal("Receipt theek hai", json.GetProperty("reviewComment").GetString());
        Assert.Equal(manager.Id, json.GetProperty("reviewedByUserId").GetString());

        var stored = await ExpenseApi.FindAsync(_factory, expense.Id);
        Assert.NotNull(stored);
        Assert.Equal(ExpenseStatus.Approved, stored!.Status);
        Assert.Equal("Receipt theek hai", stored.ReviewComment);
        Assert.Equal(manager.Id, stored.ReviewedByUserId);
        Assert.NotNull(stored.ReviewedAtUtc);

        var audit = Assert.Single(await ExpenseApi.AuditsAsync(_factory, expense.Id), a => a.Action == "ExpenseApproved");
        Assert.Equal(manager.Id, audit.UserId);
        Assert.Equal("Receipt theek hai", audit.Details);
    }

    [Fact]
    public async Task Admin_rejects_someone_elses_expense_with_comment_and_audit()
    {
        var expense = await SubmittedExpenseOfNewEmployeeAsync();
        var client = await TestAuth.AdminClientAsync(_factory);
        var adminId = await TestAuth.GetAdminIdAsync(_factory);

        var response = await client.PostAsJsonAsync(
            $"/api/Expenses/{expense.Id}/reject", new { comment = "Receipt nahi lagi" });

        await response.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Equal(ExpenseStatus.Rejected, (await response.ReadJsonAsync()).GetProperty("status").GetString());

        var stored = await ExpenseApi.FindAsync(_factory, expense.Id);
        Assert.Equal(ExpenseStatus.Rejected, stored!.Status);
        Assert.Equal("Receipt nahi lagi", stored.ReviewComment);
        Assert.Equal(adminId, stored.ReviewedByUserId);

        var audit = Assert.Single(await ExpenseApi.AuditsAsync(_factory, expense.Id), a => a.Action == "ExpenseRejected");
        Assert.Equal(adminId, audit.UserId);
        Assert.Equal("Receipt nahi lagi", audit.Details);
    }

    [Fact]
    public async Task Approve_without_body_works_and_stores_no_comment()
    {
        var expense = await SubmittedExpenseOfNewEmployeeAsync();
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);

        var response = await client.PostAsync($"/api/Expenses/{expense.Id}/approve", null);

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var json = await response.ReadJsonAsync();
        Assert.Equal(ExpenseStatus.Approved, json.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("reviewComment").ValueKind);

        var stored = await ExpenseApi.FindAsync(_factory, expense.Id);
        Assert.Equal(ExpenseStatus.Approved, stored!.Status);
        Assert.Null(stored.ReviewComment);
        Assert.Single(await ExpenseApi.AuditsAsync(_factory, expense.Id), a => a.Action == "ExpenseApproved");
    }

    [Fact]
    public async Task Reject_with_empty_json_object_works()
    {
        var expense = await SubmittedExpenseOfNewEmployeeAsync();
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);

        // {} -> ReviewRequest jis ka Comment null hai
        using var empty = new StringContent("{}", Encoding.UTF8, "application/json");
        var response = await client.PostAsync($"/api/Expenses/{expense.Id}/reject", empty);

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var stored = await ExpenseApi.FindAsync(_factory, expense.Id);
        Assert.Equal(ExpenseStatus.Rejected, stored!.Status);
        Assert.Null(stored.ReviewComment);
        Assert.Single(await ExpenseApi.AuditsAsync(_factory, expense.Id), a => a.Action == "ExpenseRejected");
    }

    [Fact]
    public async Task Reject_with_null_comment_like_the_react_client_works()
    {
        var expense = await SubmittedExpenseOfNewEmployeeAsync();
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);

        var response = await client.PostAsJsonAsync(
            $"/api/Expenses/{expense.Id}/reject", new { comment = (string?)null });

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var stored = await ExpenseApi.FindAsync(_factory, expense.Id);
        Assert.Equal(ExpenseStatus.Rejected, stored!.Status);
        Assert.Null(stored.ReviewComment);
    }

    [Fact]
    public async Task Approving_or_rejecting_own_expense_returns_400()
    {
        var (manager, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var own = await TestData.AddExpenseAsync(_factory, manager.Id, ExpenseStatus.Submitted);

        var approve = await client.PostAsJsonAsync($"/api/Expenses/{own.Id}/approve", new { comment = "Khud" });
        await approve.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.False(string.IsNullOrWhiteSpace(await approve.ReadMessageAsync()));

        var reject = await ExpenseApi.ReviewAsync(client, own.Id, "reject");
        await reject.AssertStatusAsync(HttpStatusCode.BadRequest);

        var stored = await ExpenseApi.FindAsync(_factory, own.Id);
        Assert.Equal(ExpenseStatus.Submitted, stored!.Status);
        Assert.Null(stored.ReviewedByUserId);
        Assert.Empty(await ExpenseApi.AuditsAsync(_factory, own.Id));
    }

    [Fact]
    public async Task Employee_cannot_approve_or_reject_returns_403()
    {
        var expense = await SubmittedExpenseOfNewEmployeeAsync();
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);

        await (await client.PostAsJsonAsync($"/api/Expenses/{expense.Id}/approve", new { comment = "ok" }))
            .AssertStatusAsync(HttpStatusCode.Forbidden);
        await (await ExpenseApi.ReviewAsync(client, expense.Id, "reject"))
            .AssertStatusAsync(HttpStatusCode.Forbidden);

        var stored = await ExpenseApi.FindAsync(_factory, expense.Id);
        Assert.Equal(ExpenseStatus.Submitted, stored!.Status);
        Assert.Empty(await ExpenseApi.AuditsAsync(_factory, expense.Id));
    }

    [Fact]
    public async Task Approve_without_token_returns_401()
    {
        var expense = await SubmittedExpenseOfNewEmployeeAsync();
        var client = _factory.CreateClient();

        var response = await ExpenseApi.ReviewAsync(client, expense.Id, "approve");

        await response.AssertStatusAsync(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData(ExpenseStatus.Draft)]
    [InlineData(ExpenseStatus.Approved)]
    [InlineData(ExpenseStatus.Rejected)]
    public async Task Only_submitted_expense_can_be_reviewed(string status)
    {
        var employee = await TestAuth.CreateUserAsync(_factory, Roles.Employee);
        var expense = await TestData.AddExpenseAsync(_factory, employee.Id, status);
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);

        await (await ExpenseApi.ReviewAsync(client, expense.Id, "approve", "Late"))
            .AssertStatusAsync(HttpStatusCode.BadRequest);
        await (await ExpenseApi.ReviewAsync(client, expense.Id, "reject", "Late"))
            .AssertStatusAsync(HttpStatusCode.BadRequest);

        var stored = await ExpenseApi.FindAsync(_factory, expense.Id);
        Assert.Equal(status, stored!.Status);
        Assert.Empty(await ExpenseApi.AuditsAsync(_factory, expense.Id));
    }

    [Fact]
    public async Task Approved_expense_cannot_be_rejected_afterwards()
    {
        var expense = await SubmittedExpenseOfNewEmployeeAsync();
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        await (await ExpenseApi.ReviewAsync(client, expense.Id, "approve"))
            .AssertStatusAsync(HttpStatusCode.OK);

        var reject = await ExpenseApi.ReviewAsync(client, expense.Id, "reject", "Galti se approve hua");

        await reject.AssertStatusAsync(HttpStatusCode.BadRequest);
        var stored = await ExpenseApi.FindAsync(_factory, expense.Id);
        Assert.Equal(ExpenseStatus.Approved, stored!.Status);
    }

    [Fact]
    public async Task Reviewing_missing_expense_returns_404()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);

        await (await ExpenseApi.ReviewAsync(client, 987654321, "approve"))
            .AssertStatusAsync(HttpStatusCode.NotFound);
        await (await ExpenseApi.ReviewAsync(client, 987654321, "reject"))
            .AssertStatusAsync(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Comment_longer_than_500_characters_returns_400()
    {
        var expense = await SubmittedExpenseOfNewEmployeeAsync();
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);

        var response = await client.PostAsJsonAsync(
            $"/api/Expenses/{expense.Id}/approve", new { comment = new string('x', 501) });

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        var stored = await ExpenseApi.FindAsync(_factory, expense.Id);
        Assert.Equal(ExpenseStatus.Submitted, stored!.Status);
    }

    [Fact]
    public async Task Full_flow_employee_submits_and_manager_approves()
    {
        var (employee, employeeClient) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var (_, managerClient) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var id = await ExpenseApi.CreateIdAsync(employeeClient);
        await (await employeeClient.PostAsync($"/api/Expenses/{id}/submit", null))
            .AssertStatusAsync(HttpStatusCode.OK);

        await (await managerClient.PostAsJsonAsync($"/api/Expenses/{id}/approve", new { comment = "Done" }))
            .AssertStatusAsync(HttpStatusCode.OK);

        var mine = await employeeClient.GetAsync($"/api/Expenses/{id}");
        await mine.AssertStatusAsync(HttpStatusCode.OK);
        var json = await mine.ReadJsonAsync();
        Assert.Equal(ExpenseStatus.Approved, json.GetProperty("status").GetString());
        Assert.Equal(employee.Id, json.GetProperty("submittedByUserId").GetString());

        var actions = (await ExpenseApi.AuditsAsync(_factory, id)).Select(a => a.Action).ToList();
        Assert.Equal(new[] { "ExpenseCreated", "ExpenseSubmitted", "ExpenseApproved" }, actions);
    }
}
