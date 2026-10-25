namespace AiCeo.Desktop;

/// <summary>
/// The one place the rest of the app touches the UI thread.
///
/// v1.0.15 replaced Avalonia's <c>Dispatcher.UIThread</c> with PhotinoX's
/// <see cref="Photino.NET.PhotinoDispatcher"/>. Only this class knows about it: the view models and
/// the presenter call <see cref="CheckAccess"/> / <see cref="Post"/>, so nothing above this file
/// depends on a UI toolkit.
///
/// Two details matter for the bugs this release fixes:
/// <list type="number">
///   <item>The dispatcher thread is recorded when <see cref="Attach"/> runs, which is the same
///   thread that later runs the native message loop. <see cref="CheckAccess"/> therefore answers
///   correctly before the loop starts, without asking the native side.</item>
///   <item>A post that cannot be scheduled (the loop is not running yet, or it has already exited)
///   falls through to the thread pool instead of being silently dropped. Work disappearing here is
///   what turns into "the window is up but empty".</item>
/// </list>
/// </summary>
internal static class UiThread
{
    private static Photino.NET.PhotinoDispatcher? _dispatcher;
    private static int _threadId;

    /// <summary>True once the UI thread has been recorded.</summary>
    internal static bool IsAttached => Volatile.Read(ref _threadId) != 0;

    /// <summary>Binds the app to the application dispatcher. Call on the message-loop thread.</summary>
    internal static void Attach(Photino.NET.PhotinoDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        Volatile.Write(ref _threadId, Environment.CurrentManagedThreadId);
    }

    /// <summary>True when the caller is on the recorded UI thread.</summary>
    internal static bool CheckAccess()
    {
        var threadId = Volatile.Read(ref _threadId);
        return threadId != 0 && threadId == Environment.CurrentManagedThreadId;
    }

    /// <summary>
    /// Runs <paramref name="action"/> on the UI thread. Executes inline when already there.
    /// Never throws and never drops the work: if the dispatcher cannot take it, it runs on the
    /// thread pool.
    /// </summary>
    internal static void Post(Action action)
    {
        if (TryPost(action))
        {
            return;
        }

        ThreadPool.QueueUserWorkItem(_ => Guard(action));
    }

    /// <summary>Schedules <paramref name="action"/>; false when it could not be scheduled.</summary>
    internal static bool TryPost(Action action)
    {
        if (CheckAccess())
        {
            Guard(action);
            return true;
        }

        var dispatcher = _dispatcher;

        if (dispatcher is null)
        {
            return false;
        }

        try
        {
            return dispatcher.BeginInvoke(() => Guard(action));
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> on the UI thread and completes when it finishes. The delegate
    /// is *started* on the UI thread, so every <c>await</c> inside it resumes there as well — which
    /// is what keeps the agent/feed collections single-threaded while the presenter serialises them.
    ///
    /// Use this for work that touches view-model state from a background task. If the message loop
    /// is not running yet (the window is created before the loop starts), the start is retried
    /// instead of being dropped, and only as a last resort does it run where the caller is.
    /// </summary>
    internal static Task RunAsync(Func<Task> work)
        => RunAsync(async () =>
        {
            await work();
            return true;
        });

    /// <inheritdoc cref="RunAsync(Func{Task})"/>
    internal static Task<T> RunAsync<T>(Func<Task<T>> work)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Start()
        {
            try
            {
                work().ContinueWith(
                    finished =>
                    {
                        if (finished.IsCanceled)
                        {
                            completion.TrySetCanceled();
                        }
                        else if (finished.IsFaulted)
                        {
                            completion.TrySetException(finished.Exception!);
                        }
                        else
                        {
                            completion.TrySetResult(finished.Result);
                        }
                    },
                    TaskScheduler.Default);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                completion.TrySetException(exception);
            }
        }

        if (!TryPost(Start))
        {
            ScheduleWhenRunning(Start);
        }

        return completion.Task;
    }

    /// <summary>
    /// Retries a post until the dispatcher accepts it (the loop is running), for at most ten
    /// seconds, then runs it on the thread pool. Nothing is ever silently discarded.
    /// </summary>
    private static void ScheduleWhenRunning(Action action)
        => ThreadPool.QueueUserWorkItem(_ =>
        {
            for (var attempt = 0; attempt < 400; attempt++)
            {
                if (TryPost(action))
                {
                    return;
                }

                Thread.Sleep(25);
            }

            Guard(action);
        });

    internal static void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Program.WriteCrashLog(exception, "UI thread callback");
            Console.WriteLine("UI: callback failed: " + exception.Message);
        }
    }
}

/// <summary>
/// A <see cref="System.Threading.SynchronizationContext"/> over <see cref="UiThread.Post"/>,
/// installed on the message-loop thread by <see cref="Program"/>.
///
/// It restores the one behaviour Avalonia's dispatcher used to provide for free: an <c>await</c>
/// inside a UI-initiated call resumes on the UI thread, so the agent/feed collections are only ever
/// mutated from one thread while the presenter serialises them.
/// </summary>
internal sealed class UiSynchronizationContext : System.Threading.SynchronizationContext
{
    public override void Post(SendOrPostCallback callback, object? state) => UiThread.Post(() => callback(state));

    public override void Send(SendOrPostCallback callback, object? state)
    {
        if (UiThread.CheckAccess())
        {
            callback(state);
            return;
        }

        // Not used on any hot path, and a blocking marshalled send could deadlock a headless run,
        // so it degrades to a post.
        UiThread.Post(() => callback(state));
    }

    public override System.Threading.SynchronizationContext CreateCopy() => this;
}
