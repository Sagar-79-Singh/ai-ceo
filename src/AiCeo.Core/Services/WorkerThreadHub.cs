using Microsoft.Extensions.Logging;

namespace AiCeo.Services;

/// <summary>
/// v2.0.1 — the one-line fan-out between the orchestrator (thread pool) and the window (message
/// loop) for live worker threads.
///
/// <see cref="WorkerThreadStore"/> writes lines from whatever thread a worker is running on; the
/// page can only be repainted from the message loop. Instead of polling, the store pokes this hub
/// and the presenter schedules a push. Listeners may not throw into the orchestrator: a broken UI
/// must never stop a run.
/// </summary>
public sealed class WorkerThreadHub
{
    private readonly ILogger<WorkerThreadHub>? _logger;

    public WorkerThreadHub(ILogger<WorkerThreadHub>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>Raised after a thread was created, appended to or completed.</summary>
    public event Action<Guid>? Changed;

    /// <summary>Tells the UI that <paramref name="threadId"/> changed. Safe to call from any thread.</summary>
    public void Notify(Guid threadId)
    {
        var handler = Changed;

        if (handler is null)
        {
            return;
        }

        try
        {
            handler(threadId);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger?.LogWarning(exception, "A worker-thread listener threw");
        }
    }
}
