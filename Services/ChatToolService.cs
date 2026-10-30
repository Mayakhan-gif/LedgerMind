using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using LedgerMind.Data;
using LedgerMind.Models;
using Microsoft.EntityFrameworkCore;
using TaskStatus = LedgerMind.Models.TaskStatus;

namespace LedgerMind.Services;

// Chatbot ke read-only tools. Har tool yahan khud role check karta hai (prompt par bharosa nahi).
public class ChatToolService
{
    // Har tool zyada se zyada itni rows model ko bhejta hai
    public const int MaxRows = 20;

    // Model ko API jaisa camelCase JSON milta hai
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly AppDbContext _db;
    private readonly ILogger<ChatToolService> _logger;

    public ChatToolService(AppDbContext db, ILogger<ChatToolService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<string> ExecuteAsync(
        string name, string argsJson, ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return Serialize(new { error = "User pehchana nahi gaya." });

        var isManager = user.IsInRole(Roles.Admin) || user.IsInRole(Roles.FinanceManager);

        try
        {
            object result;

            switch (name)
            {
                case "get_overdue_invoices":
                    result = isManager
                        ? await GetOverdueInvoicesAsync(cancellationToken)
                        : new { error = "Is user ko invoices dekhne ki ijazat nahi." };
                    break;

                case "get_pending_approvals":
                    result = isManager
                        ? await GetPendingApprovalsAsync(userId, cancellationToken)
                        : new { error = "Is user ko approvals dekhne ki ijazat nahi." };
                    break;

                case "get_my_tasks":
                    result = await GetMyTasksAsync(userId, cancellationToken);
                    break;

                case "get_expense_summary":
                    result = await GetExpenseSummaryAsync(argsJson, userId, isManager, cancellationToken);
                    break;

                default:
                    result = new { error = "Ye tool maujood nahi." };
                    break;
            }

            return Serialize(result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Asal wajah sirf log me, model ko nahi
            _logger.LogWarning(ex, "Chat tool {Tool} fail hua", name);
            return Serialize(new { error = "Tool chalane me masla aaya." });
        }
    }

    private static string Serialize(object value) => JsonSerializer.Serialize(value, JsonOptions);

    private static string Day(DateTime value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private async Task<object> GetOverdueInvoicesAsync(CancellationToken cancellationToken)
    {
        // Overdue ka wahi qaida jo reports aur background job use karte hain
        var query = _db.Invoices.Where(InvoiceRules.IsOverdue(DateTime.UtcNow.Date));

        var totalCount = await query.CountAsync(cancellationToken);

        var rows = await query
            .OrderBy(i => i.DueDate)
            .ThenBy(i => i.Id)
            .Take(MaxRows)
            .Select(i => new
            {
                i.Id,
                i.InvoiceNumber,
                Customer = i.Customer!.Name,
                i.Currency,
                i.Total,
                Paid = _db.Payments.Where(p => p.InvoiceId == i.Id).Sum(p => (decimal?)p.Amount) ?? 0m,
                i.DueDate,
                i.Status
            })
            .ToListAsync(cancellationToken);

        var invoices = rows.Select(r => new
        {
            r.Id,
            r.InvoiceNumber,
            r.Customer,
            r.Currency,
            r.Total,
            r.Paid,
            Balance = r.Total - r.Paid,
            DueDate = Day(r.DueDate),
            r.Status
        }).ToList();

        return new { count = invoices.Count, totalCount, invoices };
    }

    private async Task<object> GetPendingApprovalsAsync(string userId, CancellationToken cancellationToken)
    {
        // Approvals queue jaisa: apni banayi, submit ya edit ki hui invoices aur apne expenses nahi dikhte
        var editedIds = await InvoiceAuthorship.EditedInvoiceIdsAsync(_db, userId, cancellationToken);

        var invoiceQuery = _db.Invoices.Where(i =>
            i.Status == InvoiceStatus.PendingApproval &&
            i.CreatedByUserId != userId &&
            i.SubmittedByUserId != userId &&
            !editedIds.Contains(i.Id));

        var expenseQuery = _db.Expenses.Where(e =>
            e.Status == ExpenseStatus.Submitted &&
            e.SubmittedByUserId != userId);

        var invoiceCount = await invoiceQuery.CountAsync(cancellationToken);
        var expenseCount = await expenseQuery.CountAsync(cancellationToken);

        var invoiceRows = await invoiceQuery
            .OrderBy(i => i.InvoiceDate)
            .ThenBy(i => i.Id)
            .Take(MaxRows)
            .Select(i => new
            {
                i.Id,
                i.InvoiceNumber,
                Customer = i.Customer!.Name,
                i.Currency,
                i.Total,
                i.InvoiceDate,
                i.DueDate
            })
            .ToListAsync(cancellationToken);

        var expenseRows = await expenseQuery
            .OrderBy(e => e.ExpenseDate)
            .ThenBy(e => e.Id)
            .Take(MaxRows)
            .Select(e => new { e.Id, e.Title, e.Category, e.Currency, e.Amount, e.ExpenseDate })
            .ToListAsync(cancellationToken);

        var invoices = invoiceRows.Select(i => new
        {
            i.Id,
            i.InvoiceNumber,
            i.Customer,
            i.Currency,
            i.Total,
            InvoiceDate = Day(i.InvoiceDate),
            DueDate = Day(i.DueDate)
        }).ToList();

        var expenses = expenseRows.Select(e => new
        {
            e.Id,
            e.Title,
            e.Category,
            e.Currency,
            e.Amount,
            ExpenseDate = Day(e.ExpenseDate)
        }).ToList();

        return new { invoiceCount, expenseCount, invoices, expenses };
    }

    private async Task<object> GetMyTasksAsync(string userId, CancellationToken cancellationToken)
    {
        var query = _db.Tasks.Where(t => t.AssignedToUserId == userId && t.Status != TaskStatus.Done);

        var totalCount = await query.CountAsync(cancellationToken);

        // Due date wali pehle (qareeb wali upar), baghair date wali aakhir me
        var rows = await query
            .OrderBy(t => t.DueDate == null)
            .ThenBy(t => t.DueDate)
            .ThenBy(t => t.Id)
            .Take(MaxRows)
            .Select(t => new { t.Id, t.Title, t.Priority, t.Status, t.DueDate })
            .ToListAsync(cancellationToken);

        var tasks = rows.Select(t => new
        {
            t.Id,
            t.Title,
            t.Priority,
            t.Status,
            DueDate = t.DueDate.HasValue ? Day(t.DueDate.Value) : null
        }).ToList();

        return new { count = tasks.Count, totalCount, tasks };
    }

    private async Task<object> GetExpenseSummaryAsync(
        string argsJson, string userId, bool isManager, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var year = now.Year;
        var month = now.Month;

        if (!string.IsNullOrWhiteSpace(argsJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(argsJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    year = ReadInt(doc.RootElement, "year", year);
                    month = ReadInt(doc.RootElement, "month", month);
                }
            }
            catch (JsonException)
            {
                return new { error = "Tool ke arguments sahih JSON nahi." };
            }
        }

        if (month < 1 || month > 12 || year < 2000 || year > 2100)
            return new { error = "Saal ya mahina ghalat hai." };

        // Range filter (index use ho sake), Year/Month functions nahi
        var start = new DateTime(year, month, 1);
        var end = start.AddMonths(1);

        var query = _db.Expenses.Where(e =>
            e.ExpenseDate >= start && e.ExpenseDate < end &&
            (e.Status == ExpenseStatus.Submitted || e.Status == ExpenseStatus.Approved));

        if (!isManager)
            query = query.Where(e => e.SubmittedByUserId == userId);

        var groups = await query
            .GroupBy(e => new { e.Currency, e.Category })
            .Select(g => new
            {
                g.Key.Currency,
                g.Key.Category,
                Count = g.Count(),
                Total = g.Sum(x => x.Amount)
            })
            .OrderBy(g => g.Currency)
            .ThenByDescending(g => g.Total)
            .ToListAsync(cancellationToken);

        return new
        {
            year,
            month,
            scope = isManager ? "all" : "mine",
            note = "Sirf Submitted aur Approved expenses shamil hain. Alag currencies ko jama nahi kiya gaya.",
            groups
        };
    }

    // Na diya ho (ya null) to fallback; ghalat qeemat par -1 taake range check error de
    private static int ReadInt(JsonElement args, string name, int fallback)
    {
        if (!args.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return fallback;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;

        if (value.ValueKind == JsonValueKind.String &&
            int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return parsed;

        return -1;
    }
}
