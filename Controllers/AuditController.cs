using LedgerMind.Data;
using LedgerMind.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LedgerMind.Controllers;

[Authorize(Roles = Roles.Admin)]
[ApiController]
[Route("api/[controller]")]
public class AuditController : ControllerBase
{
    private readonly AppDbContext _db;

    public AuditController(AppDbContext db)
    {
        _db = db;
    }

    // Saare filters exact match aur optional, naye pehle.
    // Misal: /api/Audit?entityType=Invoice&entityId=12 ya /api/Audit?action=InvoiceApproved&userId=...
    [HttpGet]
    public async Task<ActionResult<List<AuditLog>>> GetLatest(
        [FromQuery] string? action = null,
        [FromQuery] string? entityType = null,
        [FromQuery] string? entityId = null,
        [FromQuery] string? userId = null,
        [FromQuery] int take = 100)
    {
        take = Math.Clamp(take, 1, 500);

        var query = _db.AuditLogs.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(action))
            query = query.Where(a => a.Action == action);

        if (!string.IsNullOrWhiteSpace(entityType))
            query = query.Where(a => a.EntityType == entityType);

        if (!string.IsNullOrWhiteSpace(entityId))
            query = query.Where(a => a.EntityId == entityId);

        if (!string.IsNullOrWhiteSpace(userId))
            query = query.Where(a => a.UserId == userId);

        return await query
            .OrderByDescending(a => a.Id)
            .Take(take)
            .ToListAsync();
    }
}
