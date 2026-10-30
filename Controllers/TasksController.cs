using System.Security.Claims;
using LedgerMind.Data;
using LedgerMind.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TaskStatus = LedgerMind.Models.TaskStatus;

namespace LedgerMind.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class TasksController : ControllerBase
{
    private readonly AppDbContext _db;

    public TasksController(AppDbContext db)
    {
        _db = db;
    }

    private string CurrentUserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    private bool IsManager => User.IsInRole(Roles.Admin) || User.IsInRole(Roles.FinanceManager);

    private static readonly string[] Priorities =
        { TaskPriority.Low, TaskPriority.Medium, TaskPriority.High };

    private static readonly string[] Statuses =
        { TaskStatus.Todo, TaskStatus.InProgress, TaskStatus.Done };

    private bool CanSee(TaskItem t) =>
        IsManager || t.AssignedToUserId == CurrentUserId || t.CreatedByUserId == CurrentUserId;

    [HttpGet("mine")]
    public async Task<ActionResult<List<TaskItem>>> Mine()
    {
        return await _db.Tasks
            .Where(t => t.AssignedToUserId == CurrentUserId)
            .OrderBy(t => t.Status == TaskStatus.Done)
            .ThenBy(t => t.DueDate)
            .ToListAsync();
    }

    [HttpGet]
    public async Task<ActionResult<List<TaskItem>>> GetAll()
    {
        var query = _db.Tasks.AsQueryable();
        if (!IsManager)
            query = query.Where(t => t.AssignedToUserId == CurrentUserId || t.CreatedByUserId == CurrentUserId);

        return await query.OrderByDescending(t => t.Id).ToListAsync();
    }

    [HttpGet("overdue")]
    public async Task<ActionResult<List<TaskItem>>> Overdue()
    {
        var now = DateTime.UtcNow;
        var query = _db.Tasks.Where(t => t.DueDate != null && t.DueDate < now && t.Status != TaskStatus.Done);

        if (!IsManager)
            query = query.Where(t => t.AssignedToUserId == CurrentUserId || t.CreatedByUserId == CurrentUserId);

        return await query.OrderBy(t => t.DueDate).ToListAsync();
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<TaskItem>> GetById(int id)
    {
        var task = await _db.Tasks.FindAsync(id);
        if (task == null || !CanSee(task)) return NotFound();
        return task;
    }

    [HttpPost]
    public async Task<ActionResult<TaskItem>> Create(TaskRequest request)
    {
        if (!Priorities.Contains(request.Priority))
            return BadRequest(new { message = "Priority Low, Medium ya High honi chahiye." });

        var requestedAssignee = request.AssignedToUserId?.Trim();
        var assigneeId = string.IsNullOrEmpty(requestedAssignee)
            ? CurrentUserId
            : requestedAssignee;

        // Doosre ko task sirf manager de sakta hai
        if (assigneeId != CurrentUserId && !IsManager)
            return Forbid();

        if (!await UserExistsAsync(assigneeId))
            return BadRequest(new { message = "Ye user maujood nahi." });

        var task = new TaskItem
        {
            Title = request.Title,
            Description = request.Description,
            Priority = request.Priority,
            DueDate = request.DueDate?.ToUniversalTime(),
            AssignedToUserId = assigneeId,
            CreatedByUserId = CurrentUserId
        };

        _db.Tasks.Add(task);
        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(GetById), new { id = task.Id }, task);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, TaskRequest request)
    {
        var task = await _db.Tasks.FindAsync(id);
        if (task == null || !CanSee(task)) return NotFound();

        if (task.CreatedByUserId != CurrentUserId && !IsManager)
            return Forbid();

        if (!Priorities.Contains(request.Priority))
            return BadRequest(new { message = "Priority Low, Medium ya High honi chahiye." });

        // Assignee sirf tab badalta hai jab bheja gaya ho aur maujooda se mukhtalif ho.
        // Khali chhorne par (React form) assignee wahi rehta hai.
        var requestedAssignee = request.AssignedToUserId?.Trim();
        if (!string.IsNullOrEmpty(requestedAssignee) && requestedAssignee != task.AssignedToUserId)
        {
            if (!IsManager)
                return Forbid();

            if (!await UserExistsAsync(requestedAssignee))
                return BadRequest(new { message = "Ye user maujood nahi." });

            task.AssignedToUserId = requestedAssignee;
        }

        task.Title = request.Title;
        task.Description = request.Description;
        task.Priority = request.Priority;
        task.DueDate = request.DueDate?.ToUniversalTime();

        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("{id}/status")]
    public async Task<IActionResult> SetStatus(int id, TaskStatusRequest request)
    {
        var task = await _db.Tasks.FindAsync(id);
        if (task == null || !CanSee(task)) return NotFound();

        if (task.AssignedToUserId != CurrentUserId && !IsManager)
            return Forbid();

        if (!Statuses.Contains(request.Status))
            return BadRequest(new { message = "Status Todo, InProgress ya Done honi chahiye." });

        task.Status = request.Status;
        task.CompletedAtUtc = request.Status == TaskStatus.Done ? DateTime.UtcNow : null;

        await _db.SaveChangesAsync();
        return Ok(task);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        var task = await _db.Tasks.FindAsync(id);
        if (task == null || !CanSee(task)) return NotFound();

        if (task.CreatedByUserId != CurrentUserId && !IsManager)
            return Forbid();

        _db.Tasks.Remove(task);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    private Task<bool> UserExistsAsync(string userId) =>
        _db.Users.AnyAsync(u => u.Id == userId);
}
