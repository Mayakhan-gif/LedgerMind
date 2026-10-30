namespace LedgerMind.Models;

public static class TaskStatus
{
    public const string Todo = "Todo";
    public const string InProgress = "InProgress";
    public const string Done = "Done";
}

public static class TaskPriority
{
    public const string Low = "Low";
    public const string Medium = "Medium";
    public const string High = "High";
}

public class TaskItem
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Priority { get; set; } = TaskPriority.Medium;
    public string Status { get; set; } = TaskStatus.Todo;
    public DateTime? DueDate { get; set; }
    public string AssignedToUserId { get; set; } = string.Empty;
    public string CreatedByUserId { get; set; } = string.Empty;
    public DateTime? CompletedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}