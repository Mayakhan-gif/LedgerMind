using System.Collections.Concurrent;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;

namespace LedgerMind.Tests.Infrastructure.Fakes;

// Hangfire ki jagah: queue me daali gayi jobs yaad rakhta hai (chalata nahi).
public sealed class FakeBackgroundJobClient : IBackgroundJobClient
{
    private int _counter;

    public ConcurrentQueue<Job> CreatedJobs { get; } = new();

    public string Create(Job job, IState state)
    {
        CreatedJobs.Enqueue(job);
        return $"job-{Interlocked.Increment(ref _counter)}";
    }

    public bool ChangeState(string jobId, IState state, string expectedState) => true;
}
