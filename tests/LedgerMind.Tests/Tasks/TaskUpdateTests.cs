using System.Net;
using System.Net.Http.Json;
using LedgerMind.Models;
using LedgerMind.Tests.Infrastructure;
using TaskStatus = LedgerMind.Models.TaskStatus;

namespace LedgerMind.Tests.Tasks;

// PUT: kaun edit kar sakta hai, aur assignee badalna (reassign) sirf manager ka kaam
public class TaskUpdateTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public TaskUpdateTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Creator_updates_title_priority_and_due_date_without_touching_assignee()
    {
        var (employee, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var task = await TestData.AddTaskAsync(_factory, employee.Id, employee.Id);
        var due = new DateTime(2026, 12, 1, 12, 0, 0, DateTimeKind.Utc);

        // React form assignedToUserId nahi bhejta
        var response = await client.PutAsJsonAsync($"/api/Tasks/{task.Id}", new
        {
            title = "Naya title",
            description = "Detail",
            priority = TaskPriority.High,
            dueDate = due
        });

        await response.AssertStatusAsync(HttpStatusCode.NoContent);
        var stored = await TaskApi.FindAsync(_factory, task.Id);
        Assert.NotNull(stored);
        Assert.Equal("Naya title", stored!.Title);
        Assert.Equal("Detail", stored.Description);
        Assert.Equal(TaskPriority.High, stored.Priority);
        Assert.Equal(due, stored.DueDate);
        Assert.Equal(employee.Id, stored.AssignedToUserId);
        Assert.Equal(TaskStatus.Todo, stored.Status);
    }

    [Fact]
    public async Task Employee_sending_own_id_as_assignee_is_not_a_reassign()
    {
        var (employee, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var task = await TestData.AddTaskAsync(_factory, employee.Id, employee.Id);

        var response = await client.PutAsJsonAsync($"/api/Tasks/{task.Id}", new
        {
            title = "Same assignee",
            priority = TaskPriority.Low,
            assignedToUserId = employee.Id
        });

        await response.AssertStatusAsync(HttpStatusCode.NoContent);
        var stored = await TaskApi.FindAsync(_factory, task.Id);
        Assert.Equal("Same assignee", stored!.Title);
        Assert.Equal(employee.Id, stored.AssignedToUserId);
    }

    [Fact]
    public async Task Manager_reassigns_task_via_put()
    {
        var (_, managerClient) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var (first, firstClient) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var (second, secondClient) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var adminId = await TestAuth.GetAdminIdAsync(_factory);
        var task = await TestData.AddTaskAsync(_factory, first.Id, adminId);

        var response = await managerClient.PutAsJsonAsync($"/api/Tasks/{task.Id}", new
        {
            title = "Reassigned",
            priority = TaskPriority.High,
            assignedToUserId = second.Id
        });

        await response.AssertStatusAsync(HttpStatusCode.NoContent);
        var stored = await TaskApi.FindAsync(_factory, task.Id);
        Assert.Equal(second.Id, stored!.AssignedToUserId);
        Assert.Equal("Reassigned", stored.Title);
        Assert.Equal(adminId, stored.CreatedByUserId);

        // Naya assignee dekh sakta hai, purana ab nahi (na creator hai na assignee)
        Assert.Contains(task.Id, await TaskApi.ListIdsAsync(secondClient, "/api/Tasks/mine"));
        await (await firstClient.GetAsync($"/api/Tasks/{task.Id}")).AssertStatusAsync(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Employee_reassigning_own_task_via_put_returns_403()
    {
        var (employee, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var other = await TestAuth.CreateUserAsync(_factory, Roles.Employee);
        var task = await TestData.AddTaskAsync(_factory, employee.Id, employee.Id);

        var response = await client.PutAsJsonAsync($"/api/Tasks/{task.Id}", new
        {
            title = "Push to someone else",
            priority = TaskPriority.Low,
            assignedToUserId = other.Id
        });

        await response.AssertStatusAsync(HttpStatusCode.Forbidden);
        var stored = await TaskApi.FindAsync(_factory, task.Id);
        Assert.Equal(employee.Id, stored!.AssignedToUserId);
        Assert.Equal(task.Title, stored.Title);
    }

    [Fact]
    public async Task Manager_reassigning_to_unknown_user_returns_400()
    {
        var (_, managerClient) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var employee = await TestAuth.CreateUserAsync(_factory, Roles.Employee);
        var task = await TestData.AddTaskAsync(_factory, employee.Id, employee.Id);

        var response = await managerClient.PutAsJsonAsync($"/api/Tasks/{task.Id}", new
        {
            title = "Ghost owner",
            priority = TaskPriority.Low,
            assignedToUserId = Guid.NewGuid().ToString()
        });

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.False(string.IsNullOrWhiteSpace(await response.ReadMessageAsync()));
        var stored = await TaskApi.FindAsync(_factory, task.Id);
        Assert.Equal(employee.Id, stored!.AssignedToUserId);
        Assert.Equal(task.Title, stored.Title);
    }

    [Fact]
    public async Task Admin_can_reassign_task_created_by_someone_else()
    {
        var adminClient = await TestAuth.AdminClientAsync(_factory);
        var creator = await TestAuth.CreateUserAsync(_factory, Roles.Employee);
        var target = await TestAuth.CreateUserAsync(_factory, Roles.FinanceManager);
        var task = await TestData.AddTaskAsync(_factory, creator.Id, creator.Id);

        var response = await adminClient.PutAsJsonAsync($"/api/Tasks/{task.Id}", new
        {
            title = task.Title,
            priority = TaskPriority.Medium,
            assignedToUserId = target.Id
        });

        await response.AssertStatusAsync(HttpStatusCode.NoContent);
        Assert.Equal(target.Id, (await TaskApi.FindAsync(_factory, task.Id))!.AssignedToUserId);
    }

    [Fact]
    public async Task Assignee_who_is_not_creator_cannot_edit_task()
    {
        var (assignee, assigneeClient) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var adminId = await TestAuth.GetAdminIdAsync(_factory);
        var task = await TestData.AddTaskAsync(_factory, assignee.Id, adminId);

        var response = await assigneeClient.PutAsJsonAsync($"/api/Tasks/{task.Id}", new
        {
            title = "Assignee edit",
            priority = TaskPriority.Low
        });

        await response.AssertStatusAsync(HttpStatusCode.Forbidden);
        Assert.Equal(task.Title, (await TaskApi.FindAsync(_factory, task.Id))!.Title);
    }

    [Fact]
    public async Task Missing_task_update_returns_404()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);

        var response = await client.PutAsJsonAsync("/api/Tasks/987654321", new
        {
            title = "Nothing",
            priority = TaskPriority.Low
        });

        await response.AssertStatusAsync(HttpStatusCode.NotFound);
    }
}
