using System.Collections.Concurrent;
using AiCeo.Models;
using Microsoft.Extensions.Logging;

namespace AiCeo.Services;

/// <summary>
/// v2.0.0 — the human-in-the-loop gate between a worker and the user's machine.
///
/// The orchestrator calls <see cref="RequestAsync"/> before every <c>write_file</c> and
/// <c>run_command</c>. The call is parked on a <see cref="TaskCompletionSource{TResult}"/> and the
/// request is handed to whoever attached themselves (the Nexus presenter), which pushes it to the
/// page. When the user presses Approve or Reject, <see cref="Resolve"/> completes the task and the
/// worker continues — with the plan unchanged on approval, and with the rejection reason delivered
/// back to the model as the tool result on rejection.
///
/// Two rules keep this from ever wedging the app:
/// <list type="bullet">
///   <item>Nobody attached (tests, <c>--self-test</c>, a headless run) → the call is approved
///   immediately, so the orchestrator never waits for a UI that does not exist.</item>
///   <item>The run is cancelled or the window closes → the waiting decision is cancelled with it via
///   <see cref="ResolveAll"/>, so no thread is left parked.</item>
/// </list>
/// </summary>
public sealed class ApprovalCoordinator
{
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<ApprovalDecision>> _waiting = new();
    private readonly ILogger<ApprovalCoordinator>? _logger;

    private Action<PendingApproval?>? _observer;
    private PendingApproval? _current;

    public ApprovalCoordinator(ILogger<ApprovalCoordinator>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// When false the policy is bypassed for this process (the user chose to trust the swarm). The
    /// gate still exists — it is simply not armed.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>True once a UI has subscribed; without one there is nobody to ask.</summary>
    public bool IsAttached => Volatile.Read(ref _observer) is not null;

    /// <summary>True when a tool call must actually be parked for a decision.</summary>
    public bool ShouldAsk => Enabled && IsAttached;

    /// <summary>The request currently on screen, or <c>null</c>.</summary>
    public PendingApproval? Current => Volatile.Read(ref _current);

    /// <summary>Number of calls parked right now (0 or 1 with the sequential topology).</summary>
    public int WaitingCount => _waiting.Count;

    /// <summary>Registers the UI that renders the approval card.</summary>
    public void Attach(Action<PendingApproval?> observer)
        => Interlocked.Exchange(ref _observer, observer ?? throw new ArgumentNullException(nameof(observer)));

    /// <summary>
    /// Parks one tool call until the user decides. Returns an approval immediately when nothing is
    /// attached, so headless runs and the test suite behave exactly as before v2.0.0.
    /// </summary>
    public async Task<ApprovalDecision> RequestAsync(
        PendingApproval request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!ShouldAsk)
        {
            return ApprovalDecision.Approve();
        }

        cancellationToken.ThrowIfCancellationRequested();

        var completion = new TaskCompletionSource<ApprovalDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiting[request.Id] = completion;

        Volatile.Write(ref _current, request);
        _logger?.LogInformation(
            "Approval requested for {Tool} on {Target} ({Id})",
            request.ToolName,
            request.Target,
            request.Id);

        Notify(request);

        using var registration = cancellationToken.Register(() =>
        {
            if (_waiting.TryRemove(request.Id, out var parked))
            {
                parked.TrySetCanceled(cancellationToken);
            }
        });

        try
        {
            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            _waiting.TryRemove(request.Id, out _);

            if (ReferenceEquals(Current, request))
            {
                Volatile.Write(ref _current, null);
                Notify(null);
            }
        }
    }

    /// <summary>Delivers the user's answer. Returns false when that request is no longer waiting.</summary>
    public bool Resolve(Guid id, bool approved, string? reason)
    {
        if (!_waiting.TryRemove(id, out var completion))
        {
            return false;
        }

        var decision = approved ? ApprovalDecision.Approve() : ApprovalDecision.Reject(reason);

        _logger?.LogInformation("Approval {Decision} for {Id}", decision.Describe(), id);
        return completion.TrySetResult(decision);
    }

    /// <summary>
    /// Answers every parked call at once — used when a run is stopped or the window closes, so a
    /// rejection reason ("the run was stopped") reaches the model instead of leaving it hanging.
    /// </summary>
    public int ResolveAll(bool approved, string? reason)
    {
        var resolved = 0;

        foreach (var id in _waiting.Keys.ToList())
        {
            if (Resolve(id, approved, reason))
            {
                resolved++;
            }
        }

        return resolved;
    }

    private void Notify(PendingApproval? request)
    {
        var observer = Volatile.Read(ref _observer);

        if (observer is null)
        {
            return;
        }

        try
        {
            observer(request);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A broken UI listener must never turn into a stuck tool call.
            _logger?.LogWarning(exception, "The approval observer threw");
        }
    }
}
