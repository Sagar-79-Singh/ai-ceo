using System.ComponentModel.DataAnnotations;

namespace AiCeo.Models;

/// <summary>
/// v2.0.0 — a named workspace: "Python Game", "React App", …
///
/// A space owns a folder (the same sandbox root the guardrail enforces), the conversation that was
/// held inside it and the flag that says which space the window should reopen with. Exactly one row
/// has <see cref="IsActive"/> set; switching spaces rewrites the workspace root, so every worker
/// follows the user to the folder they picked.
/// </summary>
public class Space
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required, MaxLength(60)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Absolute folder this space works in, or <c>null</c> before one is chosen.</summary>
    [MaxLength(1000)]
    public string? FolderPath { get; set; }

    /// <summary>True for the space the window restores on the next launch.</summary>
    public bool IsActive { get; set; }

    /// <summary>The last directive typed inside this space.</summary>
    public string LastPrompt { get; set; } = string.Empty;

    /// <summary>The last final answer produced inside this space.</summary>
    public string FinalOutput { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<SpaceFeedEntry> Feed { get; set; } = new();
}

/// <summary>One line of a space's saved conversation, mirroring the live feed item.</summary>
public class SpaceFeedEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid SpaceId { get; set; }

    public Space? Space { get; set; }

    /// <summary>Position in the feed (0-based, ascending).</summary>
    public int Ordinal { get; set; }

    /// <summary><c>plan</c>, <c>delegation</c>, <c>tool</c>, <c>result</c>, <c>error</c>, <c>approval</c> or <c>note</c>.</summary>
    [Required, MaxLength(32)]
    public string Kind { get; set; } = string.Empty;

    [MaxLength(120)]
    public string AgentName { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    [MaxLength(64)]
    public string? ToolName { get; set; }

    public bool Failed { get; set; }

    public bool Pending { get; set; }

    /// <summary>JSON array of <c>{index, role, instructions}</c> for a CEO plan card.</summary>
    public string StepsJson { get; set; } = "[]";

    /// <summary>The title shown on the card (delegation / tool state), preserved verbatim.</summary>
    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>One step of a saved CEO plan.</summary>
public sealed record SpaceStep(int Index, string Role, string Instructions);

/// <summary>A space as the UI lists it.</summary>
public sealed record SpaceView(
    Guid Id,
    string Name,
    string? FolderPath,
    bool IsActive,
    int FeedCount,
    DateTime UpdatedAt)
{
    public bool HasFolder => !string.IsNullOrWhiteSpace(FolderPath);
}

/// <summary>One restored conversation line.</summary>
public sealed record SpaceFeedItem(
    string Kind,
    string AgentName,
    string Text,
    string? ToolName,
    bool Failed,
    bool Pending,
    string Title,
    IReadOnlyList<SpaceStep> Steps);

/// <summary>A whole restored conversation: the prompt, the final answer and the feed.</summary>
public sealed record SpaceSnapshot(
    SpaceView Space,
    string LastPrompt,
    string FinalOutput,
    IReadOnlyList<SpaceFeedItem> Feed)
{
    public static SpaceSnapshot Empty(SpaceView space)
        => new(space, string.Empty, string.Empty, Array.Empty<SpaceFeedItem>());
}
