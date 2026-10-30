using System.Security.Claims;
using LedgerMind.Data;
using LedgerMind.Models;

namespace LedgerMind.Services;

public class AuditService
{
    // AuditLogs columns ki lambai (AppDbContext dekhein)
    private const int ActionLength = 100;
    private const int EntityTypeLength = 100;
    private const int EntityIdLength = 100;
    private const int DetailsLength = 1000;
    private const int UserEmailLength = 256;

    private readonly AppDbContext _db;
    private readonly IHttpContextAccessor _http;

    public AuditService(AppDbContext db, IHttpContextAccessor http)
    {
        _db = db;
        _http = http;
    }

    // Sirf context me daalta hai, save nahi karta. Caller ke SaveChanges ke saath
    // asal kaam aur audit ek hi transaction me jate hain (ya dono, ya koi nahi).
    public void Stage(
        string action,
        string entityType,
        string? entityId,
        string? details = null,
        string? userId = null,
        string? userEmail = null)
    {
        var user = _http.HttpContext?.User;

        _db.AuditLogs.Add(new AuditLog
        {
            UserId = userId ?? user?.FindFirstValue(ClaimTypes.NameIdentifier),
            UserEmail = Truncate(userEmail ?? user?.FindFirstValue(ClaimTypes.Email), UserEmailLength),
            Action = Truncate(action, ActionLength) ?? string.Empty,
            EntityType = Truncate(entityType, EntityTypeLength) ?? string.Empty,
            EntityId = Truncate(entityId, EntityIdLength),
            Details = Truncate(details, DetailsLength),
            CreatedAtUtc = DateTime.UtcNow
        });
    }

    // Akela audit (jab saath me koi aur change save na ho raha ho)
    public async Task LogAsync(
        string action,
        string entityType,
        string? entityId,
        string? details = null,
        string? userId = null,
        string? userEmail = null)
    {
        Stage(action, entityType, entityId, details, userId, userEmail);
        await _db.SaveChangesAsync();
    }

    private static string? Truncate(string? value, int max) =>
        value == null || value.Length <= max ? value : value[..max];
}
