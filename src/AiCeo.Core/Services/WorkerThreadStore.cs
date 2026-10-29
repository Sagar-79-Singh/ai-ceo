using AiCeo.Data;
using AiCeo.Models;
using Microsoft.EntityFrameworkCore;

namespace AiCeo.Services;

/// <summary>
/// v2.0.1 — reads and writes the CEO ↔ worker threads.
///
/// The orchestrator appends a line for every step of a delegation (the instruction, each thought,
/// each tool call and its result, the approval gate, the final answer), and the UI reads the thread
/// back while it runs and after a restart. Every write is bounded and defensive: recording a
/// conversation must never be the reason a run fails.
/// </summary>
public sealed class WorkerThreadStore
{
    /// <summary>Lines kept per thread; older lines are dropped from the front.</summary>
    public const int MaxEntriesPerThread = 300;

    /// <summary>Longest single line stored.</summary>
    public const int MaxEntryChars = 8_000;

    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly WorkerThreadHub _hub;

    public WorkerThreadStore(IDbContextFactory<AppDbContext> factory, WorkerThreadHub hub)
    {
        _factory = factory;
        _hub = hub;
    }

    /// <summary>Opens a thread for one delegation and records the CEO's instruction in it.</summary>
    public async Task<Guid> BeginAsync(
        Guid taskId,
        string agentName,
        string role,
        string instructions,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var thread = new AgentThread
        {
            TaskId = taskId,
            AgentName = Clip(agentName, 120, "Worker"),
            Role = Clip(role, 120, string.Empty),
            Instructions = Clip(instructions, MaxEntryChars, string.Empty),
            Status = ThreadStatus.Running,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        db.AgentThreads.Add(thread);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await AppendAsync(
            thread.Id,
            ThreadEntryKind.Instruction,
            instructions,
            toolName: null,
            failed: false,
            cancellationToken).ConfigureAwait(false);

        _hub.Notify(thread.Id);
        return thread.Id;
    }

    /// <summary>Appends one line. Silently does nothing when the thread no longer exists.</summary>
    public async Task AppendAsync(
        Guid threadId,
        ThreadEntryKind kind,
        string? text,
        string? toolName = null,
        bool failed = false,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var thread = await db.AgentThreads
            .FirstOrDefaultAsync(row => row.Id == threadId, cancellationToken)
            .ConfigureAwait(false);

        if (thread is null)
        {
            return;
        }

        var next = await db.AgentThreadEntries
            .Where(entry => entry.ThreadId == threadId)
            .Select(entry => (int?)entry.Ordinal)
            .MaxAsync(cancellationToken)
            .ConfigureAwait(false) ?? -1;

        db.AgentThreadEntries.Add(new AgentThreadEntry
        {
            ThreadId = threadId,
            Ordinal = next + 1,
            Kind = ThreadEntryKinds.ToStorage(kind),
            Text = Clip(text, MaxEntryChars, string.Empty),
            ToolName = string.IsNullOrWhiteSpace(toolName) ? null : Clip(toolName, 64, null),
            Failed = failed,
            CreatedAt = DateTime.UtcNow
        });

        thread.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Keep the thread bounded: drop the oldest lines once the cap is passed.
        var count = await db.AgentThreadEntries.CountAsync(entry => entry.ThreadId == threadId, cancellationToken)
            .ConfigureAwait(false);

        if (count > MaxEntriesPerThread)
        {
            var stale = await db.AgentThreadEntries
                .Where(entry => entry.ThreadId == threadId)
                .OrderBy(entry => entry.Ordinal)
                .Take(count - MaxEntriesPerThread)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            db.AgentThreadEntries.RemoveRange(stale);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        _hub.Notify(threadId);
    }

    /// <summary>Closes a thread with the worker's answer and a final status.</summary>
    public async Task CompleteAsync(
        Guid threadId,
        string status,
        string? output,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var thread = await db.AgentThreads
            .FirstOrDefaultAsync(row => row.Id == threadId, cancellationToken)
            .ConfigureAwait(false);

        if (thread is null)
        {
            return;
        }

        thread.Status = Clip(status, 32, ThreadStatus.Completed);
        thread.Output = Clip(output, MaxEntryChars, string.Empty);
        thread.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await AppendAsync(
            threadId,
            status == ThreadStatus.Failed ? ThreadEntryKind.Error : ThreadEntryKind.Output,
            output,
            failed: status == ThreadStatus.Failed,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        _hub.Notify(threadId);
    }

    /// <summary>The most recent threads, newest first — what the sidebar lists.</summary>
    public async Task<IReadOnlyList<AgentThreadView>> ListRecentAsync(
        int take = 20,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        return await db.AgentThreads
            .AsNoTracking()
            .OrderByDescending(thread => thread.UpdatedAt)
            .Take(Math.Clamp(take, 1, 100))
            .Select(thread => new AgentThreadView(
                thread.Id,
                thread.TaskId,
                thread.AgentName,
                thread.Role,
                thread.Status,
                thread.Instructions,
                thread.Entries.Count,
                thread.UpdatedAt))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Every thread of one worker, newest first.</summary>
    public async Task<IReadOnlyList<AgentThreadView>> ListForAgentAsync(
        string agentName,
        int take = 20,
        CancellationToken cancellationToken = default)
    {
        var name = (agentName ?? string.Empty).Trim();

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        return await db.AgentThreads
            .AsNoTracking()
            .Where(thread => thread.AgentName == name)
            .OrderByDescending(thread => thread.UpdatedAt)
            .Take(Math.Clamp(take, 1, 100))
            .Select(thread => new AgentThreadView(
                thread.Id,
                thread.TaskId,
                thread.AgentName,
                thread.Role,
                thread.Status,
                thread.Instructions,
                thread.Entries.Count,
                thread.UpdatedAt))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>The newest thread of one worker, or null when it has never been delegated to.</summary>
    public async Task<AgentThreadView?> LatestForAgentAsync(
        string agentName,
        CancellationToken cancellationToken = default)
    {
        var threads = await ListForAgentAsync(agentName, 1, cancellationToken).ConfigureAwait(false);
        return threads.Count == 0 ? null : threads[0];
    }

    /// <summary>A whole thread with every line, oldest first.</summary>
    public async Task<AgentThreadDetail?> GetAsync(
        Guid threadId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var thread = await db.AgentThreads
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == threadId, cancellationToken)
            .ConfigureAwait(false);

        if (thread is null)
        {
            return null;
        }

        var entries = await db.AgentThreadEntries
            .AsNoTracking()
            .Where(entry => entry.ThreadId == threadId)
            .OrderBy(entry => entry.Ordinal)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var view = new AgentThreadEntryView[entries.Count];

        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];

            view[index] = new AgentThreadEntryView(
                entry.Kind,
                entry.Text,
                entry.ToolName,
                entry.Failed,
                entry.CreatedAt.ToLocalTime().ToString("HH:mm:ss"));
        }

        var summary = new AgentThreadView(
            thread.Id,
            thread.TaskId,
            thread.AgentName,
            thread.Role,
            thread.Status,
            thread.Instructions,
            entries.Count,
            thread.UpdatedAt);

        return new AgentThreadDetail(summary, thread.Output, view);
    }

    private static string Clip(string? value, int max, string? fallback)
    {
        if (string.IsNullOrEmpty(value))
        {
            return fallback ?? string.Empty;
        }

        var trimmed = value.TrimEnd();

        return trimmed.Length <= max ? trimmed : trimmed[..max] + "…";
    }
}
