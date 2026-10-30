using System.Net;
using LedgerMind.Models;
using LedgerMind.Services;
using LedgerMind.Tests.Infrastructure;

namespace LedgerMind.Tests.Jobs;

// POST /api/Jobs/run-overdue-check sirf Admin ke liye, aur job Hangfire queue me jati hai (fake client)
public class JobsEndpointTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public JobsEndpointTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Admin_can_enqueue_overdue_check()
    {
        var client = await TestAuth.AdminClientAsync(_factory);

        var response = await client.PostAsync("/api/Jobs/run-overdue-check", null);

        await response.AssertStatusAsync(HttpStatusCode.Accepted);
        Assert.Contains(_factory.FakeJobs.CreatedJobs, job =>
            job.Method.DeclaringType == typeof(BackgroundJobs) &&
            job.Method.Name == nameof(BackgroundJobs.CheckOverdueInvoicesAsync));
    }

    [Fact]
    public async Task Employee_cannot_enqueue_overdue_check()
    {
        var (_, client) = await TestAuth.NewUserClientAsync(_factory, Roles.Employee);

        var response = await client.PostAsync("/api/Jobs/run-overdue-check", null);

        await response.AssertStatusAsync(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Enqueue_without_token_returns_401()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync("/api/Jobs/run-overdue-check", null);

        await response.AssertStatusAsync(HttpStatusCode.Unauthorized);
    }
}
