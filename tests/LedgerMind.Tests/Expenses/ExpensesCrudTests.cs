using System.Net;
using System.Net.Http.Json;
using LedgerMind.Models;
using LedgerMind.Tests.Infrastructure;

namespace LedgerMind.Tests.Expenses;

// Owner ke kaam: create / read / update / submit / delete, visibility aur audit
public class ExpensesCrudTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public ExpensesCrudTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Request_without_token_returns_401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/Expenses");

        await response.AssertStatusAsync(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Employee_creates_draft_owned_by_self_with_uppercased_currency()
    {
        var (employee, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);

        var created = await ExpenseApi.CreateAsync(client, "Hotel", "usd");

        Assert.Equal(ExpenseStatus.Draft, created.GetProperty("status").GetString());
        Assert.Equal(employee.Id, created.GetProperty("submittedByUserId").GetString());
        Assert.Equal("USD", created.GetProperty("currency").GetString());
        Assert.Equal("Hotel", created.GetProperty("title").GetString());

        var stored = await ExpenseApi.FindAsync(_factory, created.GetProperty("id").GetInt32());
        Assert.NotNull(stored);
        Assert.Equal("USD", stored!.Currency);
        Assert.Equal(employee.Id, stored.SubmittedByUserId);
    }

    [Fact]
    public async Task Employee_can_read_update_and_delete_own_draft()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var id = await ExpenseApi.CreateIdAsync(client);

        var get = await client.GetAsync($"/api/Expenses/{id}");
        await get.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Equal(ExpenseApi.DefaultTitle, (await get.ReadJsonAsync()).GetProperty("title").GetString());

        Assert.Contains(id, await ExpenseApi.ListIdsAsync(client));

        var update = await client.PutAsJsonAsync($"/api/Expenses/{id}", ExpenseApi.Body("Taxi updated", "EUR", 2500m));
        await update.AssertStatusAsync(HttpStatusCode.NoContent);

        var afterUpdate = await ExpenseApi.FindAsync(_factory, id);
        Assert.NotNull(afterUpdate);
        Assert.Equal("Taxi updated", afterUpdate!.Title);
        Assert.Equal("EUR", afterUpdate.Currency);
        Assert.Equal(2500m, afterUpdate.Amount);
        Assert.Equal(ExpenseStatus.Draft, afterUpdate.Status);

        var delete = await client.DeleteAsync($"/api/Expenses/{id}");
        await delete.AssertStatusAsync(HttpStatusCode.NoContent);

        var gone = await client.GetAsync($"/api/Expenses/{id}");
        await gone.AssertStatusAsync(HttpStatusCode.NotFound);
        Assert.Null(await ExpenseApi.FindAsync(_factory, id));
    }

    [Fact]
    public async Task Create_update_and_delete_write_audit_rows_for_the_owner()
    {
        var (employee, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var id = await ExpenseApi.CreateIdAsync(client);

        await (await client.PutAsJsonAsync($"/api/Expenses/{id}", ExpenseApi.Body("Edited")))
            .AssertStatusAsync(HttpStatusCode.NoContent);
        await (await client.DeleteAsync($"/api/Expenses/{id}"))
            .AssertStatusAsync(HttpStatusCode.NoContent);

        var audits = await ExpenseApi.AuditsAsync(_factory, id);

        var created = Assert.Single(audits, a => a.Action == "ExpenseCreated");
        Assert.Equal(employee.Id, created.UserId);
        Assert.Contains(ExpenseApi.DefaultTitle, created.Details ?? string.Empty);

        var updated = Assert.Single(audits, a => a.Action == "ExpenseUpdated");
        Assert.Equal(employee.Id, updated.UserId);
        Assert.Contains("Edited", updated.Details ?? string.Empty);

        var deleted = Assert.Single(audits, a => a.Action == "ExpenseDeleted");
        Assert.Equal(employee.Id, deleted.UserId);
    }

    [Fact]
    public async Task Owner_can_submit_draft_and_audits_ExpenseCreated_and_ExpenseSubmitted_exist()
    {
        var (employee, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var id = await ExpenseApi.CreateIdAsync(client);

        var response = await client.PostAsync($"/api/Expenses/{id}/submit", null);

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var json = await response.ReadJsonAsync();
        Assert.Equal(ExpenseStatus.Submitted, json.GetProperty("status").GetString());

        var stored = await ExpenseApi.FindAsync(_factory, id);
        Assert.Equal(ExpenseStatus.Submitted, stored!.Status);

        var audits = await ExpenseApi.AuditsAsync(_factory, id);
        Assert.Equal(employee.Id, Assert.Single(audits, a => a.Action == "ExpenseCreated").UserId);
        Assert.Equal(employee.Id, Assert.Single(audits, a => a.Action == "ExpenseSubmitted").UserId);
    }

    [Fact]
    public async Task Submitting_twice_returns_400()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var id = await ExpenseApi.CreateIdAsync(client);
        await (await client.PostAsync($"/api/Expenses/{id}/submit", null)).AssertStatusAsync(HttpStatusCode.OK);

        var again = await client.PostAsync($"/api/Expenses/{id}/submit", null);

        await again.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.False(string.IsNullOrWhiteSpace(await again.ReadMessageAsync()));
        Assert.Single(await ExpenseApi.AuditsAsync(_factory, id), a => a.Action == "ExpenseSubmitted");
    }

    [Fact]
    public async Task Another_employee_cannot_see_or_change_someone_elses_expense()
    {
        var (_, owner) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var (_, other) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var id = await ExpenseApi.CreateIdAsync(owner);

        await (await other.GetAsync($"/api/Expenses/{id}"))
            .AssertStatusAsync(HttpStatusCode.NotFound);
        await (await other.PutAsJsonAsync($"/api/Expenses/{id}", ExpenseApi.Body("Hijacked")))
            .AssertStatusAsync(HttpStatusCode.NotFound);
        await (await other.PostAsync($"/api/Expenses/{id}/submit", null))
            .AssertStatusAsync(HttpStatusCode.NotFound);
        await (await other.DeleteAsync($"/api/Expenses/{id}"))
            .AssertStatusAsync(HttpStatusCode.NotFound);

        Assert.DoesNotContain(id, await ExpenseApi.ListIdsAsync(other));

        var stored = await ExpenseApi.FindAsync(_factory, id);
        Assert.NotNull(stored);
        Assert.Equal(ExpenseApi.DefaultTitle, stored!.Title);
        Assert.Equal(ExpenseStatus.Draft, stored.Status);
    }

    [Fact]
    public async Task Manager_sees_everyones_expenses_but_cannot_edit_someone_elses_draft()
    {
        var (_, employee) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var (_, manager) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var id = await ExpenseApi.CreateIdAsync(employee);

        Assert.Contains(id, await ExpenseApi.ListIdsAsync(manager));
        await (await manager.GetAsync($"/api/Expenses/{id}")).AssertStatusAsync(HttpStatusCode.OK);

        // Ownership: Admin/FM bhi kisi aur ka draft edit/submit/delete nahi kar sakta
        await (await manager.PutAsJsonAsync($"/api/Expenses/{id}", ExpenseApi.Body("Manager edit")))
            .AssertStatusAsync(HttpStatusCode.NotFound);
        await (await manager.PostAsync($"/api/Expenses/{id}/submit", null))
            .AssertStatusAsync(HttpStatusCode.NotFound);
        await (await manager.DeleteAsync($"/api/Expenses/{id}"))
            .AssertStatusAsync(HttpStatusCode.NotFound);

        var stored = await ExpenseApi.FindAsync(_factory, id);
        Assert.Equal(ExpenseApi.DefaultTitle, stored!.Title);
    }

    [Fact]
    public async Task Employee_list_contains_only_own_expenses()
    {
        var (employee, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var other = await TestAuth.CreateUserAsync(_factory, Roles.Employee);
        var mine = await TestData.AddExpenseAsync(_factory, employee.Id);
        var theirs = await TestData.AddExpenseAsync(_factory, other.Id);

        var ids = await ExpenseApi.ListIdsAsync(client);

        Assert.Equal(new[] { mine.Id }, ids);
        Assert.DoesNotContain(theirs.Id, ids);
    }

    [Fact]
    public async Task Unsupported_currency_returns_400_on_create_and_update()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);

        var create = await client.PostAsJsonAsync("/api/Expenses", ExpenseApi.Body(currency: "JPY"));
        await create.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Empty(await ExpenseApi.ListIdsAsync(client));

        var id = await ExpenseApi.CreateIdAsync(client);
        var update = await client.PutAsJsonAsync($"/api/Expenses/{id}", ExpenseApi.Body(currency: "KWD"));
        await update.AssertStatusAsync(HttpStatusCode.BadRequest);

        var stored = await ExpenseApi.FindAsync(_factory, id);
        Assert.Equal("PKR", stored!.Currency);
    }

    [Fact]
    public async Task Invalid_amount_or_missing_title_returns_400()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);

        var zeroAmount = await client.PostAsJsonAsync("/api/Expenses", ExpenseApi.Body(amount: 0m));
        await zeroAmount.AssertStatusAsync(HttpStatusCode.BadRequest);

        var noTitle = await client.PostAsJsonAsync("/api/Expenses", ExpenseApi.Body(title: ""));
        await noTitle.AssertStatusAsync(HttpStatusCode.BadRequest);

        Assert.Empty(await ExpenseApi.ListIdsAsync(client));
    }

    // decimal(18,2): 2 se zyada decimal chupke se round ho jate, is liye pehle hi 400
    [Fact]
    public async Task Amount_with_more_than_two_decimals_returns_400_on_create_and_update()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);

        var create = await client.PostAsJsonAsync("/api/Expenses", ExpenseApi.Body(amount: 10.005m));
        await create.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Contains("2 se zyada decimal", await create.Content.ReadAsStringAsync());
        Assert.Empty(await ExpenseApi.ListIdsAsync(client));

        var id = await ExpenseApi.CreateIdAsync(client);
        var update = await client.PutAsJsonAsync($"/api/Expenses/{id}", ExpenseApi.Body(amount: 1499.999m));
        await update.AssertStatusAsync(HttpStatusCode.BadRequest);

        var stored = await ExpenseApi.FindAsync(_factory, id);
        Assert.Equal(1500m, stored!.Amount);
    }

    [Theory]
    [InlineData(ExpenseStatus.Submitted)]
    [InlineData(ExpenseStatus.Approved)]
    [InlineData(ExpenseStatus.Rejected)]
    public async Task Non_draft_expense_cannot_be_edited_submitted_or_deleted(string status)
    {
        var (employee, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var expense = await TestData.AddExpenseAsync(_factory, employee.Id, status);

        var update = await client.PutAsJsonAsync($"/api/Expenses/{expense.Id}", ExpenseApi.Body("Late edit"));
        await update.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.False(string.IsNullOrWhiteSpace(await update.ReadMessageAsync()));

        await (await client.PostAsync($"/api/Expenses/{expense.Id}/submit", null))
            .AssertStatusAsync(HttpStatusCode.BadRequest);
        await (await client.DeleteAsync($"/api/Expenses/{expense.Id}"))
            .AssertStatusAsync(HttpStatusCode.BadRequest);

        var stored = await ExpenseApi.FindAsync(_factory, expense.Id);
        Assert.NotNull(stored);
        Assert.Equal(status, stored!.Status);
        Assert.Equal(expense.Title, stored.Title);
        Assert.Empty(await ExpenseApi.AuditsAsync(_factory, expense.Id));
    }

    [Fact]
    public async Task Missing_expense_returns_404()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);

        await (await client.GetAsync("/api/Expenses/987654321")).AssertStatusAsync(HttpStatusCode.NotFound);
        await (await client.PutAsJsonAsync("/api/Expenses/987654321", ExpenseApi.Body()))
            .AssertStatusAsync(HttpStatusCode.NotFound);
        await (await client.DeleteAsync("/api/Expenses/987654321")).AssertStatusAsync(HttpStatusCode.NotFound);
    }
}
