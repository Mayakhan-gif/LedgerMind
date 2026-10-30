using Hangfire;
using LedgerMind.Models;
using LedgerMind.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LedgerMind.Controllers;

// Recurring jobs ko foran chalana (Hangfire queue me daal kar). Sirf Admin.
[Authorize(Roles = Roles.Admin)]
[ApiController]
[Route("api/[controller]")]
public class JobsController : ControllerBase
{
    private readonly IBackgroundJobClient _jobs;
    private readonly AuditService _audit;

    public JobsController(IBackgroundJobClient jobs, AuditService audit)
    {
        _jobs = jobs;
        _audit = audit;
    }

    [HttpPost("run-overdue-check")]
    public async Task<IActionResult> RunOverdueCheck()
    {
        var id = _jobs.Enqueue<BackgroundJobs>(j => j.CheckOverdueInvoicesAsync());
        await _audit.LogAsync("JobTriggered", "Job", id, nameof(BackgroundJobs.CheckOverdueInvoicesAsync));

        return Accepted(new { message = "Job queue me daal di gayi.", jobId = id });
    }

    [HttpPost("run-token-cleanup")]
    public async Task<IActionResult> RunTokenCleanup()
    {
        var id = _jobs.Enqueue<BackgroundJobs>(j => j.CleanupOldRefreshTokensAsync());
        await _audit.LogAsync("JobTriggered", "Job", id, nameof(BackgroundJobs.CleanupOldRefreshTokensAsync));

        return Accepted(new { message = "Token safai ki job queue me daal di gayi.", jobId = id });
    }
}
