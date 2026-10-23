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

                Console.WriteLine("UI SMOKE OK");
                return 0;
            }

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 0;
        }
        catch (Exception exception)
        {
            WriteCrashLog(exception, "Startup");
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
        var report = BuildReport(exception, context);

        foreach (var path in CandidateLogPaths())
        {
            try
            {
                File.AppendAllText(path, report, Encoding.UTF8);
                return;
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

    private static IEnumerable<string> CandidateLogPaths()
    {
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
            yield return Path.Combine(baseDirectory, CrashLogFileName);
        }

        yield return Path.Combine(Path.GetTempPath(), CrashLogFileName);
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
        builder.AppendLine("--------------------------------------------------------------------------------");
        builder.AppendLine(exception?.ToString() ?? "(no exception object was supplied)");
        builder.AppendLine();

        return builder.ToString();
    }
}
