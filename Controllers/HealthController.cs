using LedgerMind.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LedgerMind.Controllers;

// Load balancer / uptime monitor ke liye: app aur database dono ka haal
[AllowAnonymous]
[ApiController]
[Route("api/[controller]")]
public class HealthController : ControllerBase
{
    // Database jawab na de to health check zyada der na atke
    private static readonly TimeSpan DatabaseTimeout = TimeSpan.FromSeconds(5);

    private readonly AppDbContext _db;
    private readonly ILogger<HealthController> _logger;

    public HealthController(AppDbContext db, ILogger<HealthController> logger)
    {
        _db = db;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var databaseUp = false;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
            timeout.CancelAfter(DatabaseTimeout);
            databaseUp = await _db.Database.CanConnectAsync(timeout.Token);
        }
        catch (Exception) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            // Caller ne khud request band kar di: na ghalat warning, na jawab
            return new EmptyResult();
        }
        catch (Exception ex)
        {
            // Database band ya timeout: tafseel sirf log me, jawab me nahi
            _logger.LogWarning(ex, "Health check: database se rabta nahi hua");
        }

        Response.Headers.CacheControl = "no-store";

        var body = new
        {
            status = databaseUp ? "Healthy" : "Unhealthy",
            app = "LedgerMind",
            timeUtc = DateTime.UtcNow,
            database = databaseUp ? "Up" : "Down"
        };

        return databaseUp
            ? Ok(body)
            : StatusCode(StatusCodes.Status503ServiceUnavailable, body);
    }
}
