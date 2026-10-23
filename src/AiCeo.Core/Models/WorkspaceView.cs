namespace AiCeo.Models;

/// <summary>Read model for the sidebar workspace panel.</summary>
public sealed record WorkspaceView(
    string? RootPath,
    bool IsConfigured,
    bool Exists,
    bool ToolsEnabled,
    string? Problem)
{
    public string DisplayPath => string.IsNullOrWhiteSpace(RootPath) ? "No workspace selected" : RootPath!;

    public static WorkspaceView Empty(bool toolsEnabled = true)
        => new(null, false, false, toolsEnabled, null);
}
