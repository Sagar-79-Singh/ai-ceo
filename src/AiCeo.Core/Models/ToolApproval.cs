using AiCeo.Tools;

namespace AiCeo.Models;

/// <summary>
/// v2.0.0 — one tool call parked until a human decides.
///
/// Workers may still read the workspace on their own (<c>list_files</c>, <c>read_file</c>), but
/// anything that changes the user's disk or starts a process — <c>write_file</c> and
/// <c>run_command</c> — stops here first. The orchestrator awaits the decision, the Nexus page
/// renders the preview on an approval card, and the answer travels back over the same web-message
/// bridge the rest of the UI uses.
/// </summary>
public sealed class PendingApproval
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>The run this call belongs to.</summary>
    public Guid TaskId { get; init; }

    /// <summary>The worker that asked for the tool.</summary>
    public string AgentName { get; init; } = string.Empty;

    /// <summary>Canonical tool name (<c>write_file</c> or <c>run_command</c>).</summary>
    public string ToolName { get; init; } = string.Empty;

    /// <summary>The exact JSON the model produced; kept so the decision can be audited.</summary>
    public string ArgumentsJson { get; init; } = string.Empty;

    /// <summary>What the call is aimed at: a workspace-relative path, or the command line.</summary>
    public string Target { get; init; } = string.Empty;

    /// <summary>The human-readable preview: a line diff for writes, the command for commands.</summary>
    public string Preview { get; init; } = string.Empty;

    /// <summary>One line for the card header, e.g. <c>Overwrite src/Program.cs</c>.</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>True for <c>write_file</c> (the card renders a diff), false for a command.</summary>
    public bool IsWrite { get; init; }

    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>What the user answered. A rejection always carries a reason for the CEO.</summary>
public sealed record ApprovalDecision(bool Approved, string? Reason)
{
    /// <summary>Fallback reason used when the user rejects without typing anything.</summary>
    public const string DefaultRejectionReason = "the user declined this action without giving a reason.";

    public static ApprovalDecision Approve() => new(true, null);

    public static ApprovalDecision Reject(string? reason)
        => new(false, string.IsNullOrWhiteSpace(reason) ? DefaultRejectionReason : reason.Trim());

    /// <summary>One line for the feed and the logs.</summary>
    public string Describe() => Approved ? "approved" : "rejected · " + Reason;
}

/// <summary>Which tools need a human before they run.</summary>
public static class ApprovalPolicy
{
    /// <summary>
    /// <c>write_file</c> and <c>run_command</c> change the machine, so they always ask. The two
    /// read-only tools never do: stopping every <c>list_files</c> would make the swarm unusable.
    /// </summary>
    public static bool Requires(string? toolName)
    {
        var normalized = ToolCatalog.Normalize(toolName) ?? toolName;

        return string.Equals(normalized, ToolCatalog.WriteFile, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, ToolCatalog.RunCommand, StringComparison.OrdinalIgnoreCase);
    }
}
