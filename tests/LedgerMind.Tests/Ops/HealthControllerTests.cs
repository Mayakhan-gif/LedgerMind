using LedgerMind.Controllers;
using LedgerMind.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace LedgerMind.Tests.Ops;

// HealthController seedha (HTTP ke baghair): caller ki band ki hui request aur DB timeout me farq
public class HealthControllerTests
{
    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"ledgermind-health-unit-{Guid.NewGuid():N}")
            .ReplaceService<IDatabaseCreator, CancellingDatabaseCreator>()
            .Options);

    private static HealthController NewController(AppDbContext db, ListLogger<HealthController> logger, CancellationToken requestAborted) =>
        new(db, logger)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { RequestAborted = requestAborted }
            }
        };

    [Fact]
    public async Task Request_aborted_by_caller_returns_no_body_and_logs_no_warning()
    {
        await using var db = NewDb();
        var logger = new ListLogger<HealthController>();
        using var aborted = new CancellationTokenSource();
        aborted.Cancel();
        var controller = NewController(db, logger, aborted.Token);

        var result = await controller.Get();

        Assert.IsType<EmptyResult>(result);
        Assert.DoesNotContain(LogLevel.Warning, logger.Levels);
    }

    [Fact]
    public async Task Database_timeout_without_abort_returns_503_and_logs_warning()
    {
        await using var db = NewDb();
        var logger = new ListLogger<HealthController>();
        var controller = NewController(db, logger, CancellationToken.None);

        var result = await controller.Get();

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, objectResult.StatusCode);
        Assert.Contains(LogLevel.Warning, logger.Levels);
    }
}

// CanConnect hamesha OperationCanceledException deta hai (SQL Server timeout / cancel jaisa)
public sealed class CancellingDatabaseCreator : IDatabaseCreator
{
    public bool EnsureDeleted() => false;

    public Task<bool> EnsureDeletedAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);

    public bool EnsureCreated() => false;

    public Task<bool> EnsureCreatedAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);

    public bool CanConnect() => throw new OperationCanceledException("Test: database ne waqt par jawab nahi diya");

    public Task<bool> CanConnectAsync(CancellationToken cancellationToken = default) =>
        Task.FromException<bool>(new OperationCanceledException("Test: database ne waqt par jawab nahi diya"));
}

// Sirf log levels yaad rakhta hai
public sealed class ListLogger<T> : ILogger<T>
{
    private readonly object _gate = new();
    private readonly List<LogLevel> _levels = new();

    public IReadOnlyList<LogLevel> Levels
    {
        get
        {
            lock (_gate)
                return _levels.ToList();
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (_gate)
            _levels.Add(logLevel);
    }
}
