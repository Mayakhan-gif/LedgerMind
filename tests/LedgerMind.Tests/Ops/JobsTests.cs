using System.Net;
using LedgerMind.Models;
using LedgerMind.Services;
using LedgerMind.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace LedgerMind.Tests.Ops;

// POST /api/Jobs/* (sirf Admin): Hangfire queue me job (yahan FakeBackgroundJobClient)
public class JobsTests : IClassFixture<TestWebApplicationFactory>
{
    private const string OverdueCheck = nameof(BackgroundJobs.CheckOverdueInvoicesAsync);
    private const string TokenCleanup = nameof(BackgroundJobs.CleanupOldRefreshTokensAsync);

    private readonly TestWebApplicationFactory _factory;

    public JobsTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private int CountJobs(string methodName) =>
        _factory.FakeJobs.CreatedJobs.Count(j => j.Type == typeof(BackgroundJobs) && j.Method.Name == methodName);

    private async Task AssertJobEnqueuedAsync(string path, string methodName)
    {
        var client = await TestAuth.AdminClientAsync(_factory);
        var adminId = await TestAuth.GetAdminIdAsync(_factory);
        var before = CountJobs(methodName);

        var response = await client.PostAsync(path, null);

        await response.AssertStatusAsync(HttpStatusCode.Accepted);
        var json = await response.ReadJsonAsync();
        var jobId = json.GetProperty("jobId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(jobId));
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("message").GetString()));
        Assert.Equal(before + 1, CountJobs(methodName));

        // Kis admin ne kaunsi job chalayi, audit me
        var audits = await _factory.WithDbAsync(db => db.AuditLogs
            .Where(a => a.Action == "JobTriggered" && a.EntityType == "Job" && a.EntityId == jobId)
            .ToListAsync());
        var audit = Assert.Single(audits);
        Assert.Equal(adminId, audit.UserId);
        Assert.Equal(methodName, audit.Details);
    }

    [Fact]
    public async Task Admin_run_overdue_check_enqueues_job_and_returns_202()
    {
        await AssertJobEnqueuedAsync("/api/Jobs/run-overdue-check", OverdueCheck);
    }

    [Fact]
    public async Task Admin_run_token_cleanup_enqueues_job_and_returns_202()
    {
        await AssertJobEnqueuedAsync("/api/Jobs/run-token-cleanup", TokenCleanup);
    }

    [Fact]
    public async Task Each_trigger_gets_its_own_job_id()
    {
        var client = await TestAuth.AdminClientAsync(_factory);

        var first = await client.PostAsync("/api/Jobs/run-overdue-check", null);
        var second = await client.PostAsync("/api/Jobs/run-overdue-check", null);

        await first.AssertStatusAsync(HttpStatusCode.Accepted);
        await second.AssertStatusAsync(HttpStatusCode.Accepted);
        var firstId = (await first.ReadJsonAsync()).GetProperty("jobId").GetString();
        var secondId = (await second.ReadJsonAsync()).GetProperty("jobId").GetString();
        Assert.NotEqual(firstId, secondId);
    }

    [Theory]
    [InlineData("/api/Jobs/run-overdue-check", OverdueCheck)]
    [InlineData("/api/Jobs/run-token-cleanup", TokenCleanup)]
    public async Task FinanceManager_gets_403_and_nothing_is_enqueued(string path, string methodName)
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.FinanceManager);
        var before = CountJobs(methodName);

        var response = await client.PostAsync(path, null);

        await response.AssertStatusAsync(HttpStatusCode.Forbidden);
        Assert.Equal(before, CountJobs(methodName));
    }

    [Theory]
    [InlineData("/api/Jobs/run-overdue-check", OverdueCheck)]
    [InlineData("/api/Jobs/run-token-cleanup", TokenCleanup)]
    public async Task Employee_gets_403(string path, string methodName)
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);
        var before = CountJobs(methodName);

        var response = await client.PostAsync(path, null);

        await response.AssertStatusAsync(HttpStatusCode.Forbidden);
        Assert.Equal(before, CountJobs(methodName));
    }

    [Theory]
    [InlineData("/api/Jobs/run-overdue-check")]
    [InlineData("/api/Jobs/run-token-cleanup")]
    public async Task Anonymous_gets_401(string path)
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync(path, null);

        await response.AssertStatusAsync(HttpStatusCode.Unauthorized);
    }
}
