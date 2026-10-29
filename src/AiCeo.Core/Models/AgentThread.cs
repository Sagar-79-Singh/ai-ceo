using System.ComponentModel.DataAnnotations;

namespace AiCeo.Models;

/// <summary>
/// v2.0.1 — the CEO ↔ worker conversation behind one delegation.
///
/// The main chat shows what the <i>user</i> and the <i>CEO</i> said. A thread is the other half of
/// the star: the instructions the CEO wrote for one worker, everything that worker thought and did
/// (tool calls, commands, results) and the answer it handed back. It is stored per delegation so the
/// Window can reopen it later, even after a restart, and so a running delegation can be watched live.
/// </summary>
public class AgentThread
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The user request that produced this delegation.</summary>
    public Guid TaskId { get; set; }

    /// <summary>The worker this thread belongs to ("Writer", "Forge", …).</summary>
    [Required, MaxLength(120)]
    public string AgentName { get; set; } = string.Empty;

    /// <summary>The role the CEO asked for, which is how the worker was resolved.</summary>
    [MaxLength(120)]
    public string Role { get; set; } = string.Empty;

    /// <summary>The CEO's instructions for this delegation — the first message in the thread.</summary>
    public string Instructions { get; set; } = string.Empty;

    /// <summary><c>running</c>, <c>completed</c>, <c>failed</c> or <c>stopped</c>.</summary>
    [MaxLength(32)]
    public string Status { get; set; } = ThreadStatus.Running;

    /// <summary>What the worker handed back to the CEO.</summary>
    public string Output { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<AgentThreadEntry> Entries { get; set; } = new();
}

/// <summary>The states a thread can be in.</summary>
public static class ThreadStatus
{
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Stopped = "stopped";
}

/// <summary>What one line of a worker thread represents.</summary>
public enum ThreadEntryKind
{
    /// <summary>The CEO's delegation message.</summary>
    Instruction = 0,

    /// <summary>The worker's own reasoning text, between tool calls.</summary>
    Thought = 1,

    /// <summary>A tool the worker called (with its arguments).</summary>
    ToolCall = 2,

    /// <summary>What that tool returned.</summary>
    ToolResult = 3,

    /// <summary>The approval gate asked the user.</summary>
    Approval = 4,

    /// <summary>The user answered that request.</summary>
    ApprovalResult = 5,

    /// <summary>The worker's final answer to the CEO.</summary>
    Output = 6,

    /// <summary>An error: the delegation failed.</summary>
    Error = 7,

    /// <summary>Anything else worth keeping.</summary>
    Note = 8
}

/// <summary>One stored line of a worker thread.</summary>
public class AgentThreadEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ThreadId { get; set; }

    public AgentThread? Thread { get; set; }

    /// <summary>Position in the thread (0-based, ascending).</summary>
    public int Ordinal { get; set; }

    [Required, MaxLength(32)]
    public string Kind { get; set; } = "note";

    public string Text { get; set; } = string.Empty;

    [MaxLength(64)]
    public string? ToolName { get; set; }

    public bool Failed { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A thread as the sidebar lists it.</summary>
public sealed record AgentThreadView(
    Guid Id,
    Guid TaskId,
    string AgentName,
    string Role,
    string Status,
    string Instructions,
    int EntryCount,
    DateTime UpdatedAt)
{
    public bool IsRunning => Status == ThreadStatus.Running;
}

/// <summary>One rendered line of a thread.</summary>
public sealed record AgentThreadEntryView(
    string Kind,
    string Text,
    string? ToolName,
    bool Failed,
    string At);

/// <summary>A whole thread: the header plus every line.</summary>
public sealed record AgentThreadDetail(
    AgentThreadView Thread,
    string Output,
    IReadOnlyList<AgentThreadEntryView> Entries);

/// <summary>Maps the enum to the string stored in SQLite and carried in JSON.</summary>
public static class ThreadEntryKinds
{
    public static string ToStorage(ThreadEntryKind kind) => kind switch
    {
        ThreadEntryKind.Instruction => "instruction",
        ThreadEntryKind.Thought => "thought",
        ThreadEntryKind.ToolCall => "tool_call",
        ThreadEntryKind.ToolResult => "tool_result",
        ThreadEntryKind.Approval => "approval",
        ThreadEntryKind.ApprovalResult => "approval_result",
        ThreadEntryKind.Output => "output",
        ThreadEntryKind.Error => "error",
        _ => "note"
    };
}
