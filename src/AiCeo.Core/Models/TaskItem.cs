using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AiCeo.Models;

/// <summary>
/// A single CEO run. The entity is named <c>TaskItem</c> (instead of the schema sketch's
/// <c>Task</c>) so it never collides with <see cref="System.Threading.Tasks.Task"/>; the
/// database table is still called <c>Tasks</c>.
/// </summary>
[Table("Tasks")]
public class TaskItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public string UserPrompt { get; set; } = string.Empty;

    /// <summary>JSON array of the CEO plan: <c>[{ "role": "...", "instructions": "..." }]</c>.</summary>
    public string CeoPlan { get; set; } = string.Empty;

    /// <summary>The CEO's synthesized answer shown to the user.</summary>
    public string FinalOutput { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<TaskLog> Logs { get; set; } = new();
}
