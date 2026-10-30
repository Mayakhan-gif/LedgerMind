using System.Net;
using LedgerMind.Data;
using LedgerMind.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LedgerMind.Tests.Ops;

// GET /api/Health (public)
public class HealthTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public HealthTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Health_is_public_and_reports_database_up()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/Health");

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var json = await response.ReadJsonAsync();
        Assert.Equal("Healthy", json.GetProperty("status").GetString());
        Assert.Equal("LedgerMind", json.GetProperty("app").GetString());
        Assert.Equal("Up", json.GetProperty("database").GetString());
        Assert.True(json.TryGetProperty("timeUtc", out var time));
        Assert.True(time.TryGetDateTime(out _));
    }

    [Fact]
    public async Task Health_response_is_not_cached()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/Health");

        await response.AssertStatusAsync(HttpStatusCode.OK);
        Assert.True(response.Headers.CacheControl?.NoStore == true);
    }
}

// Database tak rabta na ho saka (CanConnect exception deta hai)
public sealed class DatabaseDownFactory : TestWebApplicationFactory
{
    private readonly string _databaseName = $"ledgermind-health-down-{Guid.NewGuid():N}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.AddDbContext<AppDbContext>(options => options
                .UseInMemoryDatabase(_databaseName)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .ReplaceService<IDatabaseCreator, UnreachableDatabaseCreator>());
        });
    }
}

// Sirf CanConnect fail hota hai; InMemory store baqi sab kaam (seed, login) ke liye chalta rehta hai
public sealed class UnreachableDatabaseCreator : IDatabaseCreator
{
    public bool EnsureDeleted() => false;

    public Task<bool> EnsureDeletedAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);

    public bool EnsureCreated() => false;

    public Task<bool> EnsureCreatedAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);

    public bool CanConnect() => throw new InvalidOperationException("Test: database band hai");

    public Task<bool> CanConnectAsync(CancellationToken cancellationToken = default) =>
        Task.FromException<bool>(new InvalidOperationException("Test: database band hai"));
}

public class HealthDatabaseDownTests : IClassFixture<DatabaseDownFactory>
{
    private readonly DatabaseDownFactory _factory;

    public HealthDatabaseDownTests(DatabaseDownFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Health_returns_503_when_database_is_unreachable()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/Health");

        await response.AssertStatusAsync(HttpStatusCode.ServiceUnavailable);
        var json = await response.ReadJsonAsync();
        Assert.Equal("Unhealthy", json.GetProperty("status").GetString());
        Assert.Equal("LedgerMind", json.GetProperty("app").GetString());
        Assert.Equal("Down", json.GetProperty("database").GetString());

        // Exception ki tafseel jawab me nahi jani chahiye
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("database band hai", raw);
    }
}
