using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LedgerMind.Models;
using LedgerMind.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using TaskStatus = LedgerMind.Models.TaskStatus;

namespace LedgerMind.Tests.Tasks;

// Create, visibility, lists (/mine, /overdue), status aur delete ke qaide
public class TasksTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public TasksTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Request_without_token_returns_401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/Tasks");

        await response.AssertStatusAsync(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Employee_creates_own_task_assigned_to_self_by_default()
    {
        var (employee, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);

        var json = await TaskApi.CreateAsync(client, new
        {
            title = "Receipts jama karo",
            description = "October ke receipts",
            priority = TaskPriority.High,
            dueDate = DateTime.UtcNow.AddDays(3)
        });

        var id = json.GetProperty("id").GetInt32();
        Assert.Equal("Receipts jama karo", json.GetProperty("title").GetString());
        Assert.Equal(TaskPriority.High, json.GetProperty("priority").GetString());
        Assert.Equal(TaskStatus.Todo, json.GetProperty("status").GetString());
        Assert.Equal(employee.Id, json.GetProperty("assignedToUserId").GetString());
        Assert.Equal(employee.Id, json.GetProperty("createdByUserId").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("completedAtUtc").ValueKind);

        var get = await client.GetAsync($"/api/Tasks/{id}");
        await get.AssertStatusAsync(HttpStatusCode.OK);

        var stored = await TaskApi.FindAsync(_factory, id);
        Assert.NotNull(stored);
        Assert.Equal(employee.Id, stored!.AssignedToUserId);
        Assert.NotNull(stored.DueDate);
    }

    [Fact]
    public async Task Employee_assigning_task_to_someone_else_returns_403()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var other = await TestAuth.CreateUserAsync(_factory, Roles.Employee);

        var response = await client.PostAsJsonAsync("/api/Tasks", new
        {
            title = "Not allowed",
            priority = TaskPriority.Low,
            assignedToUserId = other.Id
        });

        await response.AssertStatusAsync(HttpStatusCode.Forbidden);
        var count = await _factory.WithDbAsync<int>(db => db.Tasks.CountAsync(t => t.AssignedToUserId == other.Id));
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Manager_assigns_task_to_another_user_who_can_then_see_it()
    {
        var (manager, managerClient) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var (employee, employeeClient) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);

        var json = await TaskApi.CreateAsync(managerClient, new
        {
            title = "Vendor ko call karo",
            priority = TaskPriority.Medium,
            assignedToUserId = employee.Id
        });

        var id = json.GetProperty("id").GetInt32();
        Assert.Equal(employee.Id, json.GetProperty("assignedToUserId").GetString());
        Assert.Equal(manager.Id, json.GetProperty("createdByUserId").GetString());

        await (await employeeClient.GetAsync($"/api/Tasks/{id}")).AssertStatusAsync(HttpStatusCode.OK);
        Assert.Contains(id, await TaskApi.ListIdsAsync(employeeClient, "/api/Tasks/mine"));
        Assert.Contains(id, await TaskApi.ListIdsAsync(employeeClient, "/api/Tasks"));
    }

    [Fact]
    public async Task Creating_task_for_unknown_user_returns_400()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var ghostId = Guid.NewGuid().ToString();

        var response = await client.PostAsJsonAsync("/api/Tasks", new
        {
            title = "Ghost",
            priority = TaskPriority.Low,
            assignedToUserId = ghostId
        });

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.False(string.IsNullOrWhiteSpace(await response.ReadMessageAsync()));
        var count = await _factory.WithDbAsync<int>(db => db.Tasks.CountAsync(t => t.AssignedToUserId == ghostId));
        Assert.Equal(0, count);
    }

    [Theory]
    [InlineData("Urgent")]
    [InlineData("high")]
    [InlineData("")]
    public async Task Invalid_priority_returns_400_on_create(string priority)
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);

        var response = await client.PostAsJsonAsync("/api/Tasks", new { title = "Bad priority", priority });

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Empty(await TaskApi.ListIdsAsync(client, "/api/Tasks"));
    }

    [Fact]
    public async Task Invalid_priority_returns_400_on_update()
    {
        var (employee, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var task = await TestData.AddTaskAsync(_factory, employee.Id, employee.Id);

        var response = await client.PutAsJsonAsync($"/api/Tasks/{task.Id}", new { title = "Changed", priority = "Urgent" });

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        var stored = await TaskApi.FindAsync(_factory, task.Id);
        Assert.Equal(task.Title, stored!.Title);
        Assert.Equal(TaskPriority.Medium, stored.Priority);
    }

    [Fact]
    public async Task Employee_sees_only_assigned_or_created_tasks_while_manager_sees_all()
    {
        var (employee, employeeClient) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var (_, managerClient) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var other = await TestAuth.CreateUserAsync(_factory, Roles.Employee);
        var adminId = await TestAuth.GetAdminIdAsync(_factory);

        var assignedToMe = await TestData.AddTaskAsync(_factory, employee.Id, adminId);
        var createdByMe = await TestData.AddTaskAsync(_factory, other.Id, employee.Id);
        var notMine = await TestData.AddTaskAsync(_factory, other.Id, adminId);

        var employeeIds = await TaskApi.ListIdsAsync(employeeClient, "/api/Tasks");
        Assert.Equal(2, employeeIds.Count);
        Assert.Contains(assignedToMe.Id, employeeIds);
        Assert.Contains(createdByMe.Id, employeeIds);
        Assert.DoesNotContain(notMine.Id, employeeIds);

        await (await employeeClient.GetAsync($"/api/Tasks/{assignedToMe.Id}")).AssertStatusAsync(HttpStatusCode.OK);
        await (await employeeClient.GetAsync($"/api/Tasks/{createdByMe.Id}")).AssertStatusAsync(HttpStatusCode.OK);
        await (await employeeClient.GetAsync($"/api/Tasks/{notMine.Id}")).AssertStatusAsync(HttpStatusCode.NotFound);

        var managerIds = await TaskApi.ListIdsAsync(managerClient, "/api/Tasks");
        Assert.Contains(assignedToMe.Id, managerIds);
        Assert.Contains(createdByMe.Id, managerIds);
        Assert.Contains(notMine.Id, managerIds);
        await (await managerClient.GetAsync($"/api/Tasks/{notMine.Id}")).AssertStatusAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Hidden_task_cannot_be_changed_by_outsider_returns_404()
    {
        var (_, outsider) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var owner = await TestAuth.CreateUserAsync(_factory, Roles.Employee);
        var task = await TestData.AddTaskAsync(_factory, owner.Id, owner.Id);

        await (await outsider.PutAsJsonAsync($"/api/Tasks/{task.Id}", new { title = "x", priority = TaskPriority.Low }))
            .AssertStatusAsync(HttpStatusCode.NotFound);
        await (await TaskApi.SetStatusAsync(outsider, task.Id, TaskStatus.Done))
            .AssertStatusAsync(HttpStatusCode.NotFound);
        await (await outsider.DeleteAsync($"/api/Tasks/{task.Id}"))
            .AssertStatusAsync(HttpStatusCode.NotFound);

        var stored = await TaskApi.FindAsync(_factory, task.Id);
        Assert.Equal(TaskStatus.Todo, stored!.Status);
    }

    [Fact]
    public async Task Status_change_allowed_for_assignee_and_manager_but_not_for_creator_who_is_not_assignee()
    {
        var (assignee, assigneeClient) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var (creator, creatorClient) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var (_, managerClient) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var task = await TestData.AddTaskAsync(_factory, assignee.Id, creator.Id);

        var byAssignee = await TaskApi.SetStatusAsync(assigneeClient, task.Id, TaskStatus.InProgress);
        await byAssignee.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Equal(TaskStatus.InProgress, (await byAssignee.ReadJsonAsync()).GetProperty("status").GetString());

        var byCreator = await TaskApi.SetStatusAsync(creatorClient, task.Id, TaskStatus.Done);
        await byCreator.AssertStatusAsync(HttpStatusCode.Forbidden);
        Assert.Equal(TaskStatus.InProgress, (await TaskApi.FindAsync(_factory, task.Id))!.Status);

        var byManager = await TaskApi.SetStatusAsync(managerClient, task.Id, TaskStatus.Done);
        await byManager.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Equal(TaskStatus.Done, (await TaskApi.FindAsync(_factory, task.Id))!.Status);
    }

    [Fact]
    public async Task Done_sets_completedAtUtc_and_back_to_Todo_clears_it()
    {
        var (employee, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var task = await TestData.AddTaskAsync(_factory, employee.Id, employee.Id);
        var before = DateTime.UtcNow.AddSeconds(-5);

        var done = await TaskApi.SetStatusAsync(client, task.Id, TaskStatus.Done);
        await done.AssertStatusAsync(HttpStatusCode.OK);
        var doneJson = await done.ReadJsonAsync();
        Assert.Equal(TaskStatus.Done, doneJson.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.String, doneJson.GetProperty("completedAtUtc").ValueKind);

        var storedDone = await TaskApi.FindAsync(_factory, task.Id);
        Assert.NotNull(storedDone!.CompletedAtUtc);
        Assert.True(storedDone.CompletedAtUtc >= before);

        var todo = await TaskApi.SetStatusAsync(client, task.Id, TaskStatus.Todo);
        await todo.AssertStatusAsync(HttpStatusCode.OK);
        var todoJson = await todo.ReadJsonAsync();
        Assert.Equal(TaskStatus.Todo, todoJson.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, todoJson.GetProperty("completedAtUtc").ValueKind);

        var storedTodo = await TaskApi.FindAsync(_factory, task.Id);
        Assert.Null(storedTodo!.CompletedAtUtc);
    }

    [Theory]
    [InlineData("Finished")]
    [InlineData("done")]
    [InlineData("")]
    public async Task Invalid_status_returns_400(string status)
    {
        var (employee, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var task = await TestData.AddTaskAsync(_factory, employee.Id, employee.Id);

        var response = await TaskApi.SetStatusAsync(client, task.Id, status);

        await response.AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Equal(TaskStatus.Todo, (await TaskApi.FindAsync(_factory, task.Id))!.Status);
    }

    [Fact]
    public async Task Creator_can_delete_but_assignee_who_is_not_creator_gets_403()
    {
        var (creator, creatorClient) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var (assignee, assigneeClient) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var task = await TestData.AddTaskAsync(_factory, assignee.Id, creator.Id);

        var byAssignee = await assigneeClient.DeleteAsync($"/api/Tasks/{task.Id}");
        await byAssignee.AssertStatusAsync(HttpStatusCode.Forbidden);
        Assert.NotNull(await TaskApi.FindAsync(_factory, task.Id));

        var byCreator = await creatorClient.DeleteAsync($"/api/Tasks/{task.Id}");
        await byCreator.AssertStatusAsync(HttpStatusCode.NoContent);
        Assert.Null(await TaskApi.FindAsync(_factory, task.Id));
    }

    [Fact]
    public async Task Manager_can_delete_any_task()
    {
        var (_, managerClient) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var employee = await TestAuth.CreateUserAsync(_factory, Roles.Employee);
        var task = await TestData.AddTaskAsync(_factory, employee.Id, employee.Id);

        var response = await managerClient.DeleteAsync($"/api/Tasks/{task.Id}");

        await response.AssertStatusAsync(HttpStatusCode.NoContent);
        Assert.Null(await TaskApi.FindAsync(_factory, task.Id));
    }

    [Fact]
    public async Task Mine_returns_only_tasks_assigned_to_me_open_first_then_by_due_date()
    {
        var (employee, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var other = await TestAuth.CreateUserAsync(_factory, Roles.Employee);
        var adminId = await TestAuth.GetAdminIdAsync(_factory);
        var now = DateTime.UtcNow;

        var later = await TestData.AddTaskAsync(_factory, employee.Id, adminId, TaskStatus.Todo, now.AddDays(5));
        var done = await TestData.AddTaskAsync(_factory, employee.Id, adminId, TaskStatus.Done, now.AddDays(-10));
        var sooner = await TestData.AddTaskAsync(_factory, employee.Id, adminId, TaskStatus.InProgress, now.AddDays(1));
        var createdForOther = await TestData.AddTaskAsync(_factory, other.Id, employee.Id);

        var ids = await TaskApi.ListIdsAsync(client, "/api/Tasks/mine");

        Assert.Equal(new[] { sooner.Id, later.Id, done.Id }, ids);
        Assert.DoesNotContain(createdForOther.Id, ids);
    }

    [Fact]
    public async Task Overdue_returns_visible_past_due_tasks_that_are_not_done()
    {
        var (employee, employeeClient) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var (_, managerClient) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var other = await TestAuth.CreateUserAsync(_factory, Roles.Employee);
        var adminId = await TestAuth.GetAdminIdAsync(_factory);
        var now = DateTime.UtcNow;

        var overdue = await TestData.AddTaskAsync(_factory, employee.Id, employee.Id, TaskStatus.Todo, now.AddDays(-2));
        var overdueDone = await TestData.AddTaskAsync(_factory, employee.Id, employee.Id, TaskStatus.Done, now.AddDays(-2));
        var future = await TestData.AddTaskAsync(_factory, employee.Id, employee.Id, TaskStatus.Todo, now.AddDays(2));
        var noDueDate = await TestData.AddTaskAsync(_factory, employee.Id, employee.Id, TaskStatus.Todo, null);
        var someoneElses = await TestData.AddTaskAsync(_factory, other.Id, adminId, TaskStatus.InProgress, now.AddDays(-3));

        var employeeIds = await TaskApi.ListIdsAsync(employeeClient, "/api/Tasks/overdue");
        Assert.Equal(new[] { overdue.Id }, employeeIds);

        var managerIds = await TaskApi.ListIdsAsync(managerClient, "/api/Tasks/overdue");
        Assert.Contains(overdue.Id, managerIds);
        Assert.Contains(someoneElses.Id, managerIds);
        Assert.DoesNotContain(overdueDone.Id, managerIds);
        Assert.DoesNotContain(future.Id, managerIds);
        Assert.DoesNotContain(noDueDate.Id, managerIds);
    }

    [Fact]
    public async Task Missing_task_returns_404()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);

        await (await client.GetAsync("/api/Tasks/987654321")).AssertStatusAsync(HttpStatusCode.NotFound);
        await (await TaskApi.SetStatusAsync(client, 987654321, TaskStatus.Done)).AssertStatusAsync(HttpStatusCode.NotFound);
        await (await client.DeleteAsync("/api/Tasks/987654321")).AssertStatusAsync(HttpStatusCode.NotFound);
    }
}
