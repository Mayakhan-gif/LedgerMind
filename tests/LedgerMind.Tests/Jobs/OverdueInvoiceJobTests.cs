using LedgerMind.Models;
using LedgerMind.Services;
using LedgerMind.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TaskStatus = LedgerMind.Models.TaskStatus;

namespace LedgerMind.Tests.Jobs;

// BackgroundJobs.CheckOverdueInvoicesAsync seedha DI se chala kar: har late invoice ke liye
// har manager ko ek reminder, duplicate nahi, aur 7 din purana Done reminder dobara banta hai.
public class OverdueInvoiceJobTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public OverdueInvoiceJobTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static string TitleFor(Invoice invoice) => $"[AUTO] Overdue invoice {invoice.InvoiceNumber}";

    private async Task RunOverdueCheckAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<BackgroundJobs>();
        await jobs.CheckOverdueInvoicesAsync();
    }

    // Admin + FinanceManager roles ke tamam users (de-duplicated)
    private async Task<HashSet<string>> ManagerIdsAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var ids = new HashSet<string>();
        foreach (var role in new[] { Roles.Admin, Roles.FinanceManager })
        {
            foreach (var user in await users.GetUsersInRoleAsync(role))
                ids.Add(user.Id);
        }

        return ids;
    }

    private Task<List<TaskItem>> TasksTitledAsync(string title) =>
        _factory.WithDbAsync<List<TaskItem>>(db => db.Tasks.AsNoTracking()
            .Where(t => t.Title == title)
            .OrderBy(t => t.Id)
            .ToListAsync());

    // dueInDays < 0 = itne din late, > 0 = abhi due nahi
    private async Task<Invoice> AddInvoiceDueAsync(
        int dueInDays, string status = InvoiceStatus.Approved, bool isExcluded = false)
    {
        var customer = await TestData.AddCustomerAsync(_factory);
        var dueDate = DateTime.UtcNow.Date.AddDays(dueInDays);
        return await TestData.AddInvoiceAsync(
            _factory,
            customer.Id,
            status: status,
            invoiceDate: dueDate.AddDays(-30),
            dueDate: dueDate,
            isExcluded: isExcluded);
    }

    [Fact]
    public async Task Creates_one_high_priority_task_per_manager_for_each_overdue_invoice()
    {
        await TestAuth.CreateUserAsync(_factory, Roles.FinanceManager);
        await TestAuth.CreateUserAsync(_factory, Roles.Employee);
        var approved = await AddInvoiceDueAsync(-5);
        var partial = await AddInvoiceDueAsync(-1, InvoiceStatus.Partial);
        var managerIds = await ManagerIdsAsync();
        Assert.True(managerIds.Count >= 2);

        await RunOverdueCheckAsync();

        foreach (var invoice in new[] { approved, partial })
        {
            var tasks = await TasksTitledAsync(TitleFor(invoice));

            Assert.Equal(managerIds.Count, tasks.Count);
            Assert.True(managerIds.SetEquals(tasks.Select(t => t.AssignedToUserId)),
                "Har manager ko theek ek task milna chahiye (employee ko nahi).");
            Assert.All(tasks, t =>
            {
                Assert.Equal(TaskPriority.High, t.Priority);
                Assert.Equal(TaskStatus.Todo, t.Status);
                Assert.Equal("system", t.CreatedByUserId);
                Assert.NotNull(t.DueDate);
                Assert.Null(t.CompletedAtUtc);
                Assert.Contains(invoice.InvoiceNumber, t.Description ?? string.Empty);
            });
        }
    }

    [Fact]
    public async Task Running_twice_creates_no_duplicates()
    {
        await TestAuth.CreateUserAsync(_factory, Roles.FinanceManager);
        var invoice = await AddInvoiceDueAsync(-3);
        var managerIds = await ManagerIdsAsync();

        await RunOverdueCheckAsync();
        await RunOverdueCheckAsync();

        var tasks = await TasksTitledAsync(TitleFor(invoice));
        Assert.Equal(managerIds.Count, tasks.Count);
        Assert.Equal(managerIds.Count, tasks.Select(t => t.AssignedToUserId).Distinct().Count());
    }

    [Fact]
    public async Task Open_reminder_in_progress_blocks_a_new_one()
    {
        var invoice = await AddInvoiceDueAsync(-4);
        var managerIds = await ManagerIdsAsync();
        await RunOverdueCheckAsync();

        var title = TitleFor(invoice);
        await _factory.WithDbAsync(async db =>
        {
            var tasks = await db.Tasks.Where(t => t.Title == title).ToListAsync();
            foreach (var task in tasks)
                task.Status = TaskStatus.InProgress;
            await db.SaveChangesAsync();
        });

        await RunOverdueCheckAsync();

        var after = await TasksTitledAsync(title);
        Assert.Equal(managerIds.Count, after.Count);
        Assert.All(after, t => Assert.Equal(TaskStatus.InProgress, t.Status));
    }

    [Fact]
    public async Task Excluded_paid_unapproved_and_not_yet_due_invoices_are_ignored()
    {
        var ignored = new[]
        {
            await AddInvoiceDueAsync(-5, isExcluded: true),
            await AddInvoiceDueAsync(-5, InvoiceStatus.Paid),
            await AddInvoiceDueAsync(-5, InvoiceStatus.Draft),
            await AddInvoiceDueAsync(-5, InvoiceStatus.PendingApproval),
            await AddInvoiceDueAsync(-5, InvoiceStatus.Rejected),
            await AddInvoiceDueAsync(-5, InvoiceStatus.Cancelled),
            await AddInvoiceDueAsync(1),
            await AddInvoiceDueAsync(10, InvoiceStatus.Partial)
        };
        var overdue = await AddInvoiceDueAsync(-2);

        await RunOverdueCheckAsync();

        foreach (var invoice in ignored)
            Assert.Empty(await TasksTitledAsync(TitleFor(invoice)));

        Assert.NotEmpty(await TasksTitledAsync(TitleFor(overdue)));
    }

    [Fact]
    public async Task Done_reminder_older_than_7_days_gets_a_new_task_but_recent_one_does_not()
    {
        await TestAuth.CreateUserAsync(_factory, Roles.FinanceManager);
        var stale = await AddInvoiceDueAsync(-20);
        var recent = await AddInvoiceDueAsync(-20);
        var managerIds = await ManagerIdsAsync();
        await RunOverdueCheckAsync();

        // Sab reminders Done: ek invoice ke 8 din pehle, doosre ke 2 din pehle
        var staleTitle = TitleFor(stale);
        var recentTitle = TitleFor(recent);
        await _factory.WithDbAsync(async db =>
        {
            var tasks = await db.Tasks
                .Where(t => t.Title == staleTitle || t.Title == recentTitle)
                .ToListAsync();
            foreach (var task in tasks)
            {
                task.Status = TaskStatus.Done;
                task.CompletedAtUtc = task.Title == staleTitle
                    ? DateTime.UtcNow.AddDays(-8)
                    : DateTime.UtcNow.AddDays(-2);
            }
            await db.SaveChangesAsync();
        });

        await RunOverdueCheckAsync();

        var staleTasks = await TasksTitledAsync(staleTitle);
        Assert.Equal(managerIds.Count * 2, staleTasks.Count);
        var fresh = staleTasks.Where(t => t.Status == TaskStatus.Todo).ToList();
        Assert.Equal(managerIds.Count, fresh.Count);
        Assert.True(managerIds.SetEquals(fresh.Select(t => t.AssignedToUserId)));
        Assert.All(fresh, t => Assert.Null(t.CompletedAtUtc));

        var recentTasks = await TasksTitledAsync(recentTitle);
        Assert.Equal(managerIds.Count, recentTasks.Count);
        Assert.All(recentTasks, t => Assert.Equal(TaskStatus.Done, t.Status));

        // Naya Todo maujood hai, is liye teesri dafa bhi kuch nahi banta
        await RunOverdueCheckAsync();
        Assert.Equal(managerIds.Count * 2, (await TasksTitledAsync(staleTitle)).Count);
        Assert.Equal(managerIds.Count, (await TasksTitledAsync(recentTitle)).Count);
    }

    [Fact]
    public async Task Only_the_manager_whose_reminder_went_stale_gets_a_new_one()
    {
        var financeManager = await TestAuth.CreateUserAsync(_factory, Roles.FinanceManager);
        var invoice = await AddInvoiceDueAsync(-15);
        var managerIds = await ManagerIdsAsync();
        await RunOverdueCheckAsync();

        var title = TitleFor(invoice);
        await _factory.WithDbAsync(async db =>
        {
            var task = await db.Tasks.SingleAsync(t => t.Title == title && t.AssignedToUserId == financeManager.Id);
            task.Status = TaskStatus.Done;
            task.CompletedAtUtc = DateTime.UtcNow.AddDays(-10);
            await db.SaveChangesAsync();
        });

        await RunOverdueCheckAsync();

        var tasks = await TasksTitledAsync(title);
        Assert.Equal(managerIds.Count + 1, tasks.Count);
        Assert.Equal(2, tasks.Count(t => t.AssignedToUserId == financeManager.Id));
        Assert.Single(tasks, t => t.AssignedToUserId == financeManager.Id && t.Status == TaskStatus.Todo);
    }

    [Fact]
    public async Task Deleted_reminder_is_recreated_on_next_run()
    {
        var financeManager = await TestAuth.CreateUserAsync(_factory, Roles.FinanceManager);
        var invoice = await AddInvoiceDueAsync(-6);
        var managerIds = await ManagerIdsAsync();
        await RunOverdueCheckAsync();

        var title = TitleFor(invoice);
        await _factory.WithDbAsync(async db =>
        {
            var task = await db.Tasks.SingleAsync(t => t.Title == title && t.AssignedToUserId == financeManager.Id);
            db.Tasks.Remove(task);
            await db.SaveChangesAsync();
        });
        Assert.Equal(managerIds.Count - 1, (await TasksTitledAsync(title)).Count);

        await RunOverdueCheckAsync();

        var tasks = await TasksTitledAsync(title);
        Assert.Equal(managerIds.Count, tasks.Count);
        Assert.Single(tasks, t => t.AssignedToUserId == financeManager.Id);
    }
}
