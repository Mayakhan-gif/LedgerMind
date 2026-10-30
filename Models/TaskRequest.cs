using System.ComponentModel.DataAnnotations;

namespace LedgerMind.Models;

public class TaskRequest
{
    [Required]
    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? Description { get; set; }

    [Required]
    public string Priority { get; set; } = TaskPriority.Medium;

    public DateTime? DueDate { get; set; }

    // Khali = create par khud ko, update par assignee wahi rehta hai.
    // Doosre user ko dena (ya badalna) sirf manager kar sakta hai.
    [MaxLength(450)]
    public string? AssignedToUserId { get; set; }
}

public class TaskStatusRequest
{
    [Required]
    public string Status { get; set; } = string.Empty;
}
