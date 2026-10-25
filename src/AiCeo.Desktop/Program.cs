using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

namespace AiCeo.Desktop;

/// <summary>
/// Process entry point.
///
/// Everything runs inside a global try/catch: if Avalonia cannot start (missing native library,
/// no display, unreadable config, …) the full stack trace is appended to <c>crash.log</c> next to
/// the executable and the process exits non-zero instead of vanishing silently.
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
            // Headless smoke test used by CI: proves the published single-file binary starts, can
            // open its database and that the workspace guardrail still refuses escapes.
            if (args.Any(argument => string.Equals(argument, "--self-test", StringComparison.OrdinalIgnoreCase)))
            {
                return SelfTest.Run();
            }

            // UI smoke test used by CI: builds the real window (XAML + DI + database) on a virtual
            // display, closes it after a couple of seconds and exits 0.
            SmokeTestMode = args.Any(argument => string.Equals(argument, "--smoke-ui", StringComparison.OrdinalIgnoreCase));

            if (SmokeTestMode)
            {
                // Default ShutdownMode is OnLastWindowClose, so closing the smoke window exits.
                var lifetime = new ClassicDesktopStyleApplicationLifetime();

                BuildAvaloniaApp().SetupWithLifetime(lifetime);

                lifetime.Start(args);

                // The window closes itself; the app sets a non-zero exit code if any assertion
                // inside the smoke run failed (missing settings panel, theme not repainted, …).
                if (Environment.ExitCode != 0)
                {
                    Console.WriteLine($"UI SMOKE FAILED (exit code {Environment.ExitCode})");
                    return Environment.ExitCode;
                }

                Console.WriteLine("UI SMOKE OK");
                return 0;
            }

            Trace("starting the Avalonia lifetime");
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            Trace("Avalonia lifetime exited cleanly");
            return 0;
        }
        catch (Exception exception)
        {
            WriteCrashLog(exception, "Startup");
            Trace("startup failed: " + exception.GetType().Name);
            return 1;
        }
    }

    /// <summary>Avalonia builder used by the runtime and by the Avalonia design tools.</summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    /// <summary>
    /// Appends a full crash report to <c>crash.log</c> in the executable's directory. Falls back to
    /// the temp folder when the application folder is read-only.
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
        Console.WriteLine("TRACE: " + message);
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
        builder.AppendLine($"bundle   : single-file = {IsSingleFileBundle}");
        builder.AppendLine($"extract  : {Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR") ?? @"(default: %TEMP%\.net)"}");
        builder.AppendLine("--------------------------------------------------------------------------------");
        builder.AppendLine(exception?.ToString() ?? "(no exception object was supplied)");
        builder.AppendLine();

        return builder.ToString();
    }
}
