namespace AiCeo.Models;

/// <summary>Canonical <c>TaskLog.Action</c> values used by the orchestrator and the feed.</summary>
public static class LogActions
{
    public const string Plan = "plan";
    public const string Delegated = "delegated";
    public const string Result = "result";
    public const string Error = "error";

    /// <summary>A worker asked for a tool (file system / terminal).</summary>
    public const string Tool = "tool";

    /// <summary>The tool returned.</summary>
    public const string ToolResult = "tool_result";
}
