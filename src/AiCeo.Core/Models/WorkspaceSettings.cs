using System.ComponentModel.DataAnnotations;

namespace AiCeo.Models;

/// <summary>
/// The single workspace the desktop app may touch. Every file and terminal tool call is confined
/// to <see cref="RootPath"/>; see <c>AiCeo.Tools.WorkspaceGuard</c>.
/// </summary>
public class WorkspaceSettings
{
    /// <summary>There is exactly one row, always id 1.</summary>
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    /// <summary>Absolute path of the selected folder, or <c>null</c> when none is selected.</summary>
    [MaxLength(1000)]
    public string? RootPath { get; set; }

    /// <summary>Global kill switch for file/terminal tools.</summary>
    public bool ToolsEnabled { get; set; } = true;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
