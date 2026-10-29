using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using AiCeo.Desktop.Nexus;
using AiCeo.Desktop.ViewModels;
using AiCeo.Models;
using AiCeo.Services;
using AiCeo.Tools;
using Microsoft.Extensions.Logging;
using Photino.NET;

namespace AiCeo.Desktop;

/// <summary>
/// Process entry point.
///
/// v1.0.15: the desktop shell is a <see cref="PhotinoWindow"/> — the operating system's own
/// WebView (WebView2 on Windows, WKWebView on macOS, WebKitGTK 4.1 on Linux) inside a native
/// window, driven by <see cref="PhotinoApplication.Run"/>. There is no Avalonia, no XAML and no
/// managed compositor anywhere in the process, which is what removes the invisible-window and
/// start-up-freeze failures of the previous shell: the loop that shows the window and the loop that
/// pumps events are the same native loop.
///
/// Everything still runs inside a global try/catch: if the shell cannot start (missing native
/// library, no display server, unreadable config, …) the full stack trace is appended to
/// <c>crash.log</c> and the process exits non-zero instead of vanishing silently.
/// </summary>
internal static class Program
{
    internal const string CrashLogFileName = "crash.log";

    /// <summary>When true the app closes itself right after the main window is shown.</summary>
    internal static bool SmokeTestMode { get; private set; }

    /// <summary>Breadcrumb file written at startup so a silent exit still leaves evidence.</summary>
    internal const string StartupLogFileName = "startup.log";

    [STAThread]
    public static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            WriteCrashLog(eventArgs.ExceptionObject as Exception, "AppDomain.UnhandledException");

        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            WriteCrashLog(eventArgs.Exception, "TaskScheduler.UnobservedTaskException");
            eventArgs.SetObserved();
        };

        Trace($"process start · pid {Environment.ProcessId} · args [{string.Join(' ', args)}]");

        try
        {
            // v1.0.12: the macOS image declares macOS 10.13 (High Sierra) as its floor. Finder obeys
            // LSMinimumSystemVersion, but a copy launched from Terminal or a package manager does
            // not, so the floor is enforced here as well: a dialog explains the problem and the
            // process exits instead of failing somewhere deep inside the runtime.
            if (IsUnsupportedMacOs())
            {
                Trace($"macOS is older than the supported {MinimumMacOs}");
                WriteCrashLog(null, "Unsupported macOS version");
                ShowMacOsVersionAlert();
                return 1;
            }

            // Headless smoke test used by CI: proves the published single-file binary starts, can
            // open its database and that the workspace guardrail still refuses escapes.
            if (args.Any(argument => string.Equals(argument, "--self-test", StringComparison.OrdinalIgnoreCase)))
            {
                return SelfTest.Run();
            }

            // UI smoke test used by CI: builds the real native window (native WebView + DI +
            // database) on a virtual display, waits for the page to talk to the host, closes it and
            // exits 0.
            SmokeTestMode = args.Any(argument => string.Equals(argument, "--smoke-ui", StringComparison.OrdinalIgnoreCase));

            return RunDesktop();
        }
        catch (Exception exception)
        {
            WriteCrashLog(exception, "Startup");
            Trace("startup failed: " + exception.GetType().Name);
            Say("STARTUP FAILED: " + exception.Message);
            return 1;
        }
    }

    /// <summary>
    /// Builds the container, the view model and the PhotinoX window, then hands control to the
    /// native message loop.
    /// </summary>
    private static int RunDesktop()
    {
        // A missing display server is reported as a proper startup failure with a stack trace,
        // rather than as an abort inside GTK. CI asserts exactly this contract.
        EnsureDisplayAvailable();

        // PhotinoX ships its native window/WebView bridge for win-x64, win-arm64, linux-x64,
        // linux-arm64, osx-x64 and osx-arm64 only. A 32-bit Windows process therefore cannot host
        // the window at all, and saying so here is far more useful than a DllNotFoundException from
        // deep inside the interop layer.
        EnsureNativeBridgeIsAvailable();

        Trace("bootstrapping the service container");
        AppBootstrap.Initialize();

        var application = new PhotinoApplication
        {
            Name = "AI CEO Orchestrator",
            ShutdownMode = PhotinoShutdownMode.OnMainWindowClose,
            NotificationsEnabled = false
        };

        // The dispatcher thread is this thread: [STAThread] on Main means PhotinoX runs the loop
        // here on every platform (it only hops to an STA thread on Windows when the caller is not
        // STA). Recording it before Run() makes UiThread.CheckAccess answer correctly during
        // start-up, and installing our SynchronizationContext keeps `await` continuations of
        // UI-initiated calls on this same thread.
        UiThread.Attach(application.Dispatcher);
        SynchronizationContext.SetSynchronizationContext(new UiSynchronizationContext());

        // A fault inside a dispatcher callback must be logged, never taken as a reason to die.
        application.DispatcherUnhandledException += (_, eventArgs) =>
            WriteCrashLog(eventArgs.ExceptionObject as Exception, "dispatcher");

        var shell = new NexusShell();

        Trace("starting the PhotinoX message loop");
        var exitCode = application.Run(shell.Window);
        Trace($"PhotinoX message loop exited with {exitCode}");

        if (SmokeTestMode && Environment.ExitCode != 0)
        {
            Say($"UI SMOKE FAILED (exit code {Environment.ExitCode})");
            return Environment.ExitCode;
        }

        return exitCode;
    }

    /// <summary>
    /// The Nexus shell: one native window whose content is the embedded HTML/WebGL console, wired
    /// to the view model through <see cref="NexusPresenter"/>.
    /// </summary>
    private sealed class NexusShell
    {
        private readonly NexusPresenter _presenter;
        private readonly MainViewModel _viewModel;
        private readonly LlmSetupProvider _connection;
        private readonly ApprovalCoordinator _approvals;
        private readonly TaskCompletionSource<bool> _backendReady =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly string _page;

        internal NexusShell()
        {
            _page = NexusAssets.RenderDocument();

            PhotinoWindow? window = null;

            // The picker needs the window, and the window needs the presenter: both are resolved
            // lazily, so neither construction order is a constraint.
            _viewModel = new MainViewModel(
                AppBootstrap.GetRequiredService<AgentDirectory>(),
                AppBootstrap.GetRequiredService<AgentOrchestrator>(),
                AppBootstrap.GetRequiredService<AgentHealthService>(),
                AppBootstrap.GetRequiredService<WorkspaceService>(),
                AppBootstrap.GetRequiredService<SpaceService>(),
                AppBootstrap.GetRequiredService<ApprovalCoordinator>(),
                new PhotinoWorkspacePicker(() => window),
                AppBootstrap.GetRequiredService<AppRuntimeInfo>(),
                AppBootstrap.LoggerFactory.CreateLogger<MainViewModel>());

            Window = new PhotinoWindow()
                .SetTitle("AI CEO Orchestrator")
                .SetSize(1440, 900)
                .SetMinSize(960, 640)
                .Center()
                .SetResizable(true)
                .SetUserDataFolder(WebViewProfileDirectory());

            window = Window;

            _connection = AppBootstrap.GetRequiredService<LlmSetupProvider>();
            _approvals = AppBootstrap.GetRequiredService<ApprovalCoordinator>();

            _presenter = new NexusPresenter(
                _viewModel,
                AppBootstrap.GetRequiredService<UserSettingsService>(),
                _connection,
                _approvals,
                AppBootstrap.GetRequiredService<AppRuntimeInfo>(),
                SendToPage);

            Window
                .LoadString(_page)
                .RegisterCreatedHandler((_, _) => OnCreated())
                .RegisterWebMessageReceivedHandler((_, eventArgs) => _presenter.Accept(eventArgs.Message))
                .RegisterClosedHandler((_, _) => _presenter.Dispose());
        }

        internal PhotinoWindow Window { get; }

        /// <summary>
        /// WebView2 keeps its profile (cookies, cache, GPU state) outside the install folder. Point
        /// it at the per-user data directory so a portable copy on a read-only USB stick still
        /// works, and so the profile sits next to the database instead of in a shared folder.
        /// Non-Windows engines have no equivalent and keep <c>null</c>.
        /// </summary>
        private static string? WebViewProfileDirectory()
        {
            if (!OperatingSystem.IsWindows())
            {
                return null;
            }

            try
            {
                var directory = Path.Combine(AppPaths.DataDirectory(), "webview");
                Directory.CreateDirectory(directory);
                return directory;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // The default (%LOCALAPPDATA%\Photino) is fine, and a failure here must not stop
                // the window from being created.
                return null;
            }
        }

        /// <summary>
        /// Hands one JSON packet to the page through the native web-message channel. PhotinoX
        /// delivers it to the callback the page registered with <c>window.external.receiveMessage</c>.
        /// </summary>
        private void SendToPage(string message)
        {
            if (Window.IsClosed || !Window.IsInitialized)
            {
                return;
            }

            Window.SendWebMessage(message);
        }

        /// <summary>
        /// The window exists and is on screen. Every slow thing — the database, the agent list, the
        /// provider probe — starts only now, on a background task, so nothing can delay or freeze
        /// the first frame.
        /// </summary>
        private void OnCreated()
        {
            Trace($"window created · {Window.Size.Width}×{Window.Size.Height} · visible={Window.IsVisible}");

            _ = Task.Run(StartBackendAsync);

            if (Program.SmokeTestMode)
            {
                _ = Task.Run(RunSmokeTestAsync);
            }
        }

        /// <summary>
        /// Everything slow, on the thread pool, in the order the UI needs it: create/upgrade the
        /// database, load agents and workspace, probe the provider, then push the first snapshot.
        /// None of it can delay the window, and none of it runs on the message loop.
        /// </summary>
        private async Task StartBackendAsync()
        {
            try
            {
                // The only blocking call: create/upgrade SQLite, on the thread pool.
                AppBootstrap.PrepareDatabase();

                // Then the view-model work, started *on the message-loop thread* so its awaits
                // resume there too — the agent/feed collections are read by the presenter on that
                // same thread. Both steps are async, so the window keeps painting throughout.
                await UiThread.RunAsync(() => _viewModel.InitializeAsync());
                await UiThread.RunAsync(() => _presenter.InitializeAsync());
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Program.WriteCrashLog(exception, "view model initialisation");
            }
            finally
            {
                _backendReady.TrySetResult(true);
            }
        }

        /// <summary>
        /// CI smoke test: proves the native window, the native WebView and the two-way bridge all
        /// work on a real (virtual) display. Any missing marker fails the build.
        /// </summary>
        private async Task RunSmokeTestAsync()
        {
            var ok = false;

            try
            {
                var ready = false;

                try
                {
                    ready = await _presenter.WaitForPageAsync().ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    ready = false;
                }

                Say($"UI SMOKE: page bridge ready = {ready}");
                if (!ready)
                {
                    Say("UI SMOKE FAILED: the Nexus page never announced itself to the host");
                }

                // The first snapshot is only complete once the database, the agent list and the
                // connection have been loaded; wait for that start-up task to finish.
                try
                {
                    await _backendReady.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    Say("UI SMOKE: WARNING start-up task did not finish within 30 s");
                }

                // The remaining checks read the window and the view model, so they run on the
                // message-loop thread.
                var checks = await UiThread.RunAsync(() => InspectAsync(ready)).ConfigureAwait(false);

                ok = ready && checks;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Say("UI SMOKE FAILED: " + exception);
                ok = false;
            }
            finally
            {
                Say(ok ? "UI SMOKE OK" : "UI SMOKE FAILED");
                Environment.ExitCode = ok ? 0 : 1;

                // Close on the message-loop thread; that is what ends Run() and returns the code.
                UiThread.Post(() =>
                {
                    if (!Window.IsClosed)
                    {
                        Window.Close();
                    }
                });
            }
        }

        /// <summary>The window, asset and round-trip assertions. Runs on the message-loop thread.</summary>
        private async Task<bool> InspectAsync(bool pageReady)
        {
            var ok = true;

            Say($"UI SMOKE: native window initialized={Window.IsInitialized} "
                + $"visible={Window.IsVisible} state={Window.WindowState}");

            // The page and its stylesheet must be inside the published executable.
            if (NexusAssets.IsValid(out var assetDetail))
            {
                Say($"UI SMOKE: nexus assets {assetDetail}");
            }
            else
            {
                Say($"UI SMOKE FAILED: the bundled Nexus assets are missing or incomplete ({assetDetail})");
                ok = false;
            }

            Say($"UI SMOKE: page document {_page.Length / 1024} KB · {_viewModel.VersionLabel} "
                + $"· agents loaded {_viewModel.Agents.Count} (page ready: {pageReady})");

            // Exercise the bridge in the other direction: a settings command must be parsed,
            // stored and reflected in the next snapshot.
            await _presenter.HandleMessageAsync(
                "{\"type\":\"settings\",\"theme\":\"Light\",\"accent\":\"Purple\",\"fontSize\":\"Large\"}");

            var state = _presenter.BuildState();
            var applied = state.Contains("\"theme\":\"Light\"", StringComparison.Ordinal)
                && state.Contains("\"accent\":\"Purple\"", StringComparison.Ordinal);

            Say($"UI SMOKE: settings round-trip = {applied}");

            if (!applied)
            {
                Say("UI SMOKE FAILED: the settings command did not reach the snapshot");
                ok = false;
            }

            // v1.0.16: a fresh database has no connection, so the page must be told to block with
            // the setup screen; a configured one must report the stored provider and model.
            var firstRun = !_connection.IsConfigured;
            Say($"UI SMOKE: first-run setup required = {firstRun} (connection: {_connection.Current.Describe()})");

            var stateJson = _presenter.BuildState();
            var connectionBlock = stateJson.Contains("\"connection\":", StringComparison.Ordinal)
                && stateJson.Contains($"\"configured\":{(firstRun ? "false" : "true")}", StringComparison.Ordinal);

            Say($"UI SMOKE: connection block in snapshot = {connectionBlock}");

            if (!connectionBlock)
            {
                Say("UI SMOKE FAILED: the snapshot does not describe the LLM connection");
                ok = false;
            }

            // Saving a connection must validate, persist and unlock the UI in one step.
            await _presenter.HandleMessageAsync(
                "{\"type\":\"saveConnection\",\"provider\":\"OmniRoute\",\"baseUrl\":\"http://localhost:20128/v1\","
                + "\"apiKey\":\"smoke-test-key-0123456789\",\"modelId\":\"smoke/model\"}");

            var configured = _connection.IsConfigured
                && _connection.Current.ModelId == "smoke/model"
                && _presenter.BuildState().Contains("\"configured\":true", StringComparison.Ordinal);

            Say($"UI SMOKE: connection round-trip = {configured}");

            if (!configured)
            {
                Say("UI SMOKE FAILED: the setup command did not persist the connection");
                ok = false;
            }

            // A rejected value must leave the stored connection untouched.
            await _presenter.HandleMessageAsync("{\"type\":\"saveConnection\",\"provider\":\"OmniRoute\",\"modelId\":\"\"}");

            var rejected = _connection.Current.ModelId == "smoke/model";
            Say($"UI SMOKE: invalid setup rejected = {rejected}");

            if (!rejected)
            {
                Say("UI SMOKE FAILED: an empty model was accepted");
                ok = false;
            }

            // v2.0.0: a space is a named workspace; creating one must persist it, make it active and
            // appear in the snapshot the header dropdown is painted from.
            await _presenter.HandleMessageAsync("{\"type\":\"createSpace\",\"name\":\"Smoke Space\"}");

            var spaceJson = _presenter.BuildState();
            var space = _viewModel.Spaces.FirstOrDefault(candidate => candidate.Name == "Smoke Space");
            var spacesOk = space is not null
                && space.IsActive
                && spaceJson.Contains("\"activeSpaceId\":\"" + space.Id + "\"", StringComparison.Ordinal)
                && spaceJson.Contains("\"spaces\":", StringComparison.Ordinal);

            Say($"UI SMOKE: spaces round-trip = {spacesOk} (space: {space?.Name ?? "none"})");

            if (!spacesOk)
            {
                Say("UI SMOKE FAILED: creating a space did not persist or publish it");
                ok = false;
            }

            // The conversation belongs to the space: saving, clearing and restoring must bring it back.
            _viewModel.Feed.Add(new FeedItemViewModel
            {
                Kind = FeedKind.Result,
                AgentName = "Smoke",
                Text = "restored line"
            });

            await _viewModel.NexusSaveConversationAsync();
            _viewModel.Feed.Clear();
            await _viewModel.NexusRestoreActiveSpaceAsync();

            var restored = _viewModel.Feed.Any(item => item.Text == "restored line");
            Say($"UI SMOKE: conversation restore = {restored} ({_viewModel.Feed.Count} lines)");

            if (!restored)
            {
                Say("UI SMOKE FAILED: the space did not restore its conversation");
                ok = false;
            }

            // v2.0.0: a write or a command parks until the page answers.
            var approvalsOk = await VerifyApprovalAsync();
            Say($"UI SMOKE: approval round-trip = {approvalsOk}");

            if (!approvalsOk)
            {
                Say("UI SMOKE FAILED: the approval gate did not park and resolve a tool call");
                ok = false;
            }

            // Leave the database the way we found it: a second smoke run must not trip over its
            // own space name, and the user's real spaces must stay untouched.
            if (space is not null)
            {
                await _presenter.HandleMessageAsync(
                    "{\"type\":\"deleteSpace\",\"id\":\"" + space.Id + "\"}");
            }

            return ok;
        }

        /// <summary>
        /// Parks a synthetic <c>run_command</c>, asserts that it is waiting (and published in the
        /// snapshot), rejects it and checks that the rejection reason comes back to the caller.
        /// </summary>
        private async Task<bool> VerifyApprovalAsync()
        {
            if (_approvals.Current is not null)
            {
                return false;
            }

            var request = new AiCeo.Models.PendingApproval
            {
                TaskId = Guid.NewGuid(),
                AgentName = "Smoke",
                ToolName = ToolCatalog.RunCommand,
                ArgumentsJson = "{\"command\":\"echo smoke\"}",
                Target = "echo smoke",
                Preview = "$ echo smoke",
                Summary = "Run a terminal command"
            };

            Task<ApprovalDecision> pending = _approvals.RequestAsync(request);

            // A call that must be approved may not complete on its own.
            if (pending.IsCompleted)
            {
                return false;
            }

            var parked = _approvals.Current?.Id == request.Id
                && _presenter.BuildState().Contains(request.Id.ToString(), StringComparison.Ordinal);

            var resolved = _approvals.Resolve(request.Id, approved: false, reason: "smoke test rejection");
            var decision = await pending;

            return parked
                && resolved
                && !decision.Approved
                && decision.Reason == "smoke test rejection"
                && _approvals.Current is null;
        }
    }

    /// <summary>
    /// Linux and the BSDs need a display server. Failing here — as a managed exception with a stack
    /// trace — keeps the <c>crash.log</c> contract deterministic instead of depending on what GTK
    /// decides to do when it cannot open the display.
    /// </summary>
    private static void EnsureDisplayAvailable()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsFreeBSD())
        {
            return;
        }

        var x11 = Environment.GetEnvironmentVariable("DISPLAY");
        var wayland = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");

        if (!string.IsNullOrWhiteSpace(x11) || !string.IsNullOrWhiteSpace(wayland))
        {
            return;
        }

        throw new InvalidOperationException(
            "No display server is available: DISPLAY and WAYLAND_DISPLAY are both empty, so the "
            + "native window cannot be created. Start the app from a desktop session, or use "
            + "--self-test for a headless check.");
    }

    /// <summary>
    /// Rejects the one architecture this build cannot work on: 32-bit Windows. Everything else —
    /// including the 32-bit Windows 10 installations that run on 64-bit hardware with the x64
    /// build — is supported by PhotinoX.
    /// </summary>
    private static void EnsureNativeBridgeIsAvailable()
    {
        if (!OperatingSystem.IsWindows() || IntPtr.Size != 4)
        {
            return;
        }

        throw new PlatformNotSupportedException(
            "This is the 32-bit (x86) build of AI CEO Orchestrator. The native window and WebView "
            + "bridge (PhotinoX.Native) is published for Windows x64 and ARM64 only, so this build "
            + "cannot open the Nexus window. Download AiCeo-win-x64.exe or Setup-win-x64.exe — 64-bit "
            + "Windows, including a 32-bit Windows installation on 64-bit hardware, runs it. The "
            + "headless --self-test still works from here.");
    }

    /// <summary>
    /// Console output that never throws: a WinExe has no console and a closed pipe must not take
    /// down a smoke run.
    /// </summary>
    private static void Say(string message)
    {
        try
        {
            Console.Out.WriteLine(message);
            Console.Out.Flush();
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // No console attached.
        }
    }

    /// <summary>
    /// Appends a full crash report to <c>crash.log</c>. Falls back to the temp folder when the
    /// application folder is read-only.
    /// </summary>
    internal static void WriteCrashLog(Exception? exception, string context)
    {
        WriteAll(CrashLogFileName, BuildReport(exception, context));
    }

    /// <summary>
    /// Appends a one-line breadcrumb to <c>startup.log</c>'s candidates. Never throws: diagnostics
    /// must not be able to break the very start-up they are supposed to explain.
    /// </summary>
    internal static void Trace(string message)
    {
        WriteAll(StartupLogFileName, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}  {message}{Environment.NewLine}");
        Say("TRACE: " + message);
    }

    /// <summary>
    /// Writes <paramref name="content"/> to every writable candidate path. Deliberately not
    /// "first one wins": a support report can then quote the copy it can actually reach.
    /// </summary>
    private static void WriteAll(string fileName, string content)
    {
        foreach (var path in CandidateLogPaths(fileName))
        {
            try
            {
                File.AppendAllText(path, content, Encoding.UTF8);
            }
            catch (Exception writeException) when (writeException is IOException
                                                         or UnauthorizedAccessException
                                                         or NotSupportedException
                                                         or ArgumentException)
            {
                // Try the next location.
            }
        }
    }

    /// <summary>
    /// Every place a diagnostic file is written to, most useful first:
    /// <list type="number">
    ///   <item><c>%APPDATA%\AiCeoOrchestrator</c> (the per-user data folder — always writable, and the
    ///   place a support request can point at);</item>
    ///   <item>the folder the executable lives in (an installed copy is usually readable);</item>
    ///   <item>the temp folder (last resort for a read-only install location).</item>
    /// </list>
    /// </summary>
    private static IEnumerable<string> CandidateLogPaths(string fileName)
    {
        // NB: the call is made before the yield, because C# forbids yielding from inside a try
        // block that has a catch clause.
        string? dataDirectory = null;

        try
        {
            dataDirectory = AppPaths.DataDirectory();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // The data folder itself is unavailable; the other two candidates still apply.
        }

        if (!string.IsNullOrWhiteSpace(dataDirectory))
        {
            yield return Path.Combine(dataDirectory, fileName);
        }

        string? baseDirectory = null;

        try
        {
            baseDirectory = AppContext.BaseDirectory;
        }
        catch (Exception)
        {
            // Fall through to the temp folder.
        }

        if (!string.IsNullOrWhiteSpace(baseDirectory))
        {
            yield return Path.Combine(baseDirectory, fileName);
        }

        yield return Path.Combine(Path.GetTempPath(), fileName);
    }

    /// <summary>
    /// The oldest macOS this build supports, matching the .dmg's <c>LSMinimumSystemVersion</c>.
    /// </summary>
    private static readonly Version MinimumMacOs = new(10, 13);

    /// <summary>
    /// True on macOS versions older than the 10.13 (High Sierra) floor declared in the .dmg.
    ///
    /// The version is read from <c>sw_vers</c> rather than <see cref="Environment.OSVersion"/>:
    /// on macOS the latter reports the Darwin kernel release (23.x on macOS 14), so comparing it
    /// directly against a macOS version would misjudge every modern Mac. If the probe fails for
    /// any reason the app is allowed to start — a diagnostic must never be the reason it refuses.
    /// </summary>
    private static bool IsUnsupportedMacOs()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return false;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "/usr/bin/sw_vers",
                ArgumentList = { "-productVersion" },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });

            if (process is null)
            {
                return false;
            }

            var text = process.StandardOutput.ReadToEnd().Trim();

            if (!process.WaitForExit(5_000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Already gone.
                }

                return false;
            }

            return Version.TryParse(text, out var version) && version < MinimumMacOs;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }
    }

    /// <summary>
    /// Best-effort native alert for the unsupported-macOS path: the app is a GUI process, so
    /// writing to stderr would show the user nothing. Never throws, never blocks for long.
    /// </summary>
    private static void ShowMacOsVersionAlert()
    {
        try
        {
            var script =
                "display alert \"AI CEO Orchestrator\" message \"This Mac runs a version of macOS older "
                + "than 10.13 (High Sierra), which AI CEO Orchestrator requires. Please update macOS, "
                + "then open the app again.\" as critical";

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "/usr/bin/osascript",
                ArgumentList = { "-e", script },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });

            process?.WaitForExit(15_000);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The exit code and the log already carry the outcome.
        }
    }

    /// <summary>
    /// True when the process is a bundled single-file executable: in that case the managed assembly
    /// has no path on disk (its native payload is extracted to a temporary folder at run time).
    /// </summary>
    private static bool IsSingleFileBundle
    {
        get
        {
            // IL3000 warns that Assembly.Location is always empty in a single-file bundle — which is
            // precisely the signal this property relies on.
#pragma warning disable IL3000
            return string.IsNullOrEmpty(typeof(Program).Assembly.Location);
#pragma warning restore IL3000
        }
    }

    private static string SafeDataDirectory()
    {
        try
        {
            return AppPaths.DataDirectory();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return "(unavailable: " + exception.Message + ")";
        }
    }

    private static string BuildReport(Exception? exception, string context)
    {
        var builder = new StringBuilder();

        builder.AppendLine("================================================================================");
        builder.AppendLine("AI CEO Orchestrator — crash report");
        builder.AppendLine($"time     : {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"context  : {context}");
        builder.AppendLine($"version  : {typeof(Program).Assembly.GetName().Version}");
        builder.AppendLine($"os       : {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        builder.AppendLine($"runtime  : {RuntimeInformation.FrameworkDescription}");
        builder.AppendLine($"process  : {Environment.ProcessPath}");
        builder.AppendLine($"base dir : {AppContext.BaseDirectory}");
        builder.AppendLine($"data dir : {SafeDataDirectory()}");
        builder.AppendLine($"display  : DISPLAY={Environment.GetEnvironmentVariable("DISPLAY") ?? "(unset)"} "
            + $"WAYLAND_DISPLAY={Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") ?? "(unset)"}");
        builder.AppendLine($"bundle   : single-file = {IsSingleFileBundle}");
        builder.AppendLine($"extract  : {Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR") ?? @"(default: %TEMP%\.net)"}");
        builder.AppendLine("--------------------------------------------------------------------------------");
        builder.AppendLine(exception?.ToString() ?? "(no exception object was supplied)");
        builder.AppendLine();

        return builder.ToString();
    }
}
