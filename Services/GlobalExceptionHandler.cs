using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;

namespace LedgerMind.Services;

// Database ke takrao (concurrency, foreign key) ko 500 ki jagah saaf 409 me badalta hai.
// Baqi har exception default ProblemDetails (500) me jati hai.
public sealed class GlobalExceptionHandler : IExceptionHandler
{
    private readonly ILogger<GlobalExceptionHandler> _logger;

    public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger)
    {
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        string message;

        switch (exception)
        {
            case DbUpdateConcurrencyException:
                _logger.LogWarning(exception, "Concurrency conflict on {Path}", httpContext.Request.Path);
                message = "Ye record isi waqt kisi aur ne badal diya. Refresh karke dobara koshish karein.";
                break;

            case DbUpdateException:
                _logger.LogWarning(exception, "Database update conflict on {Path}", httpContext.Request.Path);
                message = "Ye kaam database ki pabandi (constraint) ki wajah se nahi ho saka.";
                break;

            default:
                return false;
        }

        httpContext.Response.StatusCode = StatusCodes.Status409Conflict;
        await httpContext.Response.WriteAsJsonAsync(new { message }, cancellationToken);
        return true;
    }
}
