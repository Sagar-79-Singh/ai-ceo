using System.ComponentModel.DataAnnotations;

namespace AiCeo.Models;

/// <summary>
/// One line in the execution feed. <see cref="Action"/> is one of
/// <c>plan</c>, <c>delegated</c>, <c>result</c> or <c>error</c>.
/// </summary>
public class TaskLog
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TaskId { get; set; }

    public TaskItem? Task { get; set; }

    [Required, MaxLength(120)]
    public string AgentName { get; set; } = string.Empty;

    [Required, MaxLength(32)]
    public string Action { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
