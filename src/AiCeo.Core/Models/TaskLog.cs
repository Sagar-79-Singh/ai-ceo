using System.ComponentModel.DataAnnotations;

namespace AiCeo.Models;

/// <summary>
/// One line in the execution feed. <see cref="Action"/> is one of
/// <c>plan</c>, <c>delegated</c>, <c>result</c>, <c>error</c>, <c>tool</c> or <c>tool_result</c>.
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

    /// <summary>Tool name for <c>tool</c>/<c>tool_result</c> entries, otherwise null.</summary>
    [MaxLength(64)]
    public string? ToolName { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
