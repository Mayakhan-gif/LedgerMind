using LedgerMind.Data;
using LedgerMind.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using TaskStatus = LedgerMind.Models.TaskStatus;

namespace LedgerMind.Services;

public class BackgroundJobs
{
    // Done kiya hua reminder itne din tak naya reminder rokta hai
    public const int ReminderCooldownDays = 7;

    private const string SystemUserId = "system";

    private readonly AppDbContext _db;
    private readonly UserManager<ApplicationUser> _users;
    private readonly ILogger<BackgroundJobs> _logger;

    public BackgroundJobs(
        AppDbContext db,
        UserManager<ApplicationUser> users,
        ILogger<BackgroundJobs> logger)
    {
        _db = db;
        _users = users;
        _logger = logger;
    }

    public static string OverdueTaskTitle(string invoiceNumber) => $"[AUTO] Overdue invoice {invoiceNumber}";

    // Roz chalti hai: har late invoice ke liye har manager ko follow-up task.
    // Duplicate nahi: manager ke paas same title ka khula task ho, ya pichle 7 din me Done hua ho,
    // to naya nahi banta. Purana Done task ho aur invoice ab bhi late ho to naya reminder banta hai.
    public async Task CheckOverdueInvoicesAsync()
    {
        var now = DateTime.UtcNow;
        var today = now.Date;

        var overdue = await _db.Invoices
            .Where(InvoiceRules.IsOverdue(today))
            .OrderBy(i => i.DueDate)
            .Select(i => new { i.InvoiceNumber, i.DueDate })
            .ToListAsync();

        if (overdue.Count == 0)
        {
            _logger.LogInformation("Overdue check: koi late invoice nahi.");
            return;
        }

        var managers = new Dictionary<string, ApplicationUser>();
        foreach (var role in new[] { Roles.Admin, Roles.FinanceManager })
        {
            foreach (var u in await _users.GetUsersInRoleAsync(role))
                managers[u.Id] = u;
        }

        if (managers.Count == 0)
        {
            _logger.LogWarning("Overdue check: {Count} late invoices, lekin koi Admin/FinanceManager nahi. Task nahi bani.",
                overdue.Count);
            return;
        }

        var titles = overdue.Select(o => OverdueTaskTitle(o.InvoiceNumber)).Distinct().ToList();
        var recentCutoff = now.AddDays(-ReminderCooldownDays);

        // Jo reminder abhi "zinda" hai: khula (Done nahi) ya haal hi me Done hua
        var active = await _db.Tasks
            .Where(t => titles.Contains(t.Title) &&
                        (t.Status != TaskStatus.Done ||
                         (t.CompletedAtUtc != null && t.CompletedAtUtc >= recentCutoff)))
            .Select(t => new { t.Title, t.AssignedToUserId })
            .ToListAsync();

        var existingKeys = active
            .Select(e => Key(e.AssignedToUserId, e.Title))
            .ToHashSet();

        var created = 0;

        foreach (var inv in overdue)
        {
            var title = OverdueTaskTitle(inv.InvoiceNumber);

            foreach (var manager in managers.Values)
            {
                // Add bhi isi set me, taake ek hi run me dobara na bane
                if (!existingKeys.Add(Key(manager.Id, title))) continue;

                _db.Tasks.Add(new TaskItem
                {
                    Title = title,
                    Description = $"Invoice {inv.InvoiceNumber} ki due date {inv.DueDate:yyyy-MM-dd} guzar chuki hai. Customer se rabta karein.",
                    Priority = TaskPriority.High,
                    Status = TaskStatus.Todo,
                    DueDate = now,
                    AssignedToUserId = manager.Id,
                    CreatedByUserId = SystemUserId
                });
                created++;
            }
        }

        if (created > 0)
            await _db.SaveChangesAsync();

        _logger.LogInformation(
            "Overdue check mukammal: {Invoices} late invoices, {Managers} managers, {Created} nayi tasks bani.",
            overdue.Count, managers.Count, created);
    }

    // Haftawar: purane tokens delete
    public async Task CleanupOldRefreshTokensAsync()
    {
        var limit = DateTime.UtcNow.AddDays(-30);

        var deleted = await _db.RefreshTokens
            .Where(t => t.ExpiresAtUtc < limit ||
                        (t.RevokedAtUtc != null && t.RevokedAtUtc < limit))
            .ExecuteDeleteAsync();

        _logger.LogInformation("Token safai: {Deleted} purane tokens delete hue.", deleted);
    }

    private static string Key(string userId, string title) => $"{userId}|{title}";
}
