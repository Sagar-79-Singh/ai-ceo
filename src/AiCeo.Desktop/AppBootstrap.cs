using System.Text;
using AiCeo.Data;
using AiCeo.DependencyInjection;
using AiCeo.Models;
using AiCeo.Options;
using AiCeo.Services;
using AiCeo.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AiCeo.Desktop;

/// <summary>
/// One place that builds the service container for the desktop host and prepares the SQLite file.
/// All orchestrator/tool/provider services come from <c>AiCeo.Core</c> and are unchanged.
/// </summary>
internal static class AppBootstrap
{
    private static IServiceScope? _scope;

    /// <summary>SQLite file used by this run.</summary>
    internal static string DatabasePath { get; private set; } = string.Empty;

    /// <summary>Why the database could not be prepared, if that happened.</summary>
    internal static string? StartupWarning { get; private set; }

    /// <summary>Services the UI resolves from (a single long-lived scope for the app's lifetime).</summary>
    internal static IServiceProvider Services { get; private set; } = null!;

    internal static ILoggerFactory LoggerFactory { get; private set; } = null!;

    /// <summary>
    /// The theme/accent/text-size row, read once before the Avalonia dispatcher exists.
    ///
    /// v1.0.11: the shell used to fetch this from SQLite on the UI thread during
    /// <c>OnFrameworkInitializationCompleted</c>, which blocks the thread that is about to show the
    /// window. Reading it earlier — while there is no <see cref="SynchronizationContext"/> to deadlock
    /// on — lets the window be created and shown without a single blocking call on the UI thread.
    /// </summary>
    internal static UserSettingsSnapshot? PreloadedSettings { get; private set; }

    /// <summary>True once the container exists, so the pre-start and in-app calls cannot double-build it.</summary>
    internal static bool IsInitialized => _scope is not null;

    internal static void Initialize()
    {
        if (IsInitialized)
        {
            return;
        }

        var dataDirectory = AppPaths.DataDirectory();
        DatabasePath = DatabasePaths.ResolveFromEnvironment(dataDirectory);

        var services = new ServiceCollection();

        var logDirectory = Path.Combine(dataDirectory, "logs");
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddProvider(new FileLoggerProvider(Path.Combine(logDirectory, "aiceo.log")));
        });

        services.AddAiCeoCore(DatabasePath, new OmniRouteOptions());

        var root = services.BuildServiceProvider();
        LoggerFactory = root.GetRequiredService<ILoggerFactory>();

        // The core services are scoped; one long-lived scope is enough because each call creates
        // its own DbContext from the factory.
        _scope = root.CreateScope();
        Services = _scope.ServiceProvider;

        var logger = LoggerFactory.CreateLogger("Startup");

        try
        {
            Services.InitializeDatabaseAsync(logger).GetAwaiter().GetResult();
            logger.LogInformation("Database ready at {Path}", DatabasePath);
        }
        catch (Exception exception)
        {
            StartupWarning = $"The database could not be prepared: {exception.Message}";
            logger.LogError(exception, "Database initialisation failed ({Path})", DatabasePath);
        }
    }

    /// <summary>
    /// Prepares the database and reads the saved appearance, so the window can be shown immediately
    /// when the Avalonia lifetime starts. Never throws: a broken profile must not stop the launch.
    /// </summary>
    internal static void Preload()
    {
        try
        {
            Initialize();

            PreloadedSettings = GetRequiredService<UserSettingsService>()
                .GetAsync()
                .GetAwaiter()
                .GetResult();

            Program.Trace(
                $"settings preloaded: theme={PreloadedSettings.Theme} accent={PreloadedSettings.AccentColor} "
                + $"font={PreloadedSettings.FontSize}");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            PreloadedSettings = null;
            Program.Trace("settings could not be preloaded; the default palette will be used: " + exception.GetType().Name);
        }
    }

    internal static T GetRequiredService<T>() where T : notnull => Services.GetRequiredService<T>();

    /// <summary>Runs the guardrail/database self check used by <c>--self-test</c> and CI.</summary>
    internal static IEnumerable<string> RunDiagnostics()
    {
        Initialize();

        yield return $"database: {DatabasePath}";
        yield return $"database exists: {File.Exists(DatabasePath)}";

        if (StartupWarning is not null)
        {
            yield return $"warning: {StartupWarning}";
        }

        using var workspace = new TempFolder();

        var allowed = WorkspaceGuard.TryResolve(workspace.Path, "src/Program.cs", out var resolved, out var allowError);
        yield return $"guard allows relative path: {allowed} ({resolved}) {(allowError is null ? string.Empty : "error: " + allowError)}";

        var escaped = WorkspaceGuard.TryResolve(workspace.Path, "../escape.txt", out _, out var escapeError);
        yield return $"guard rejects traversal: {!escaped} ({escapeError})";

        var absolute = WorkspaceGuard.TryResolve(
            workspace.Path,
            Path.Combine(Path.GetTempPath(), "outside.txt"),
            out _,
            out var absoluteError);
        yield return $"guard rejects absolute path: {!absolute} ({absoluteError})";

        yield return $"workspace root readable: {Directory.Exists(workspace.Path)}";
    }

    /// <summary>Temporary folder that deletes itself.</summary>
    private sealed class TempFolder : IDisposable
    {
        public TempFolder()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aiceo-selftest", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // Best effort.
            }
        }
    }
}

/// <summary>Headless verification of a published build, wired to <c>--self-test</c>.</summary>
internal static class SelfTest
{
    internal static int Run()
    {
        var report = new StringBuilder();
        var ok = true;

        try
        {
            foreach (var line in AppBootstrap.RunDiagnostics())
            {
                report.AppendLine(line);
                if (line.Contains("guard rejects", StringComparison.Ordinal) && line.Contains("False", StringComparison.Ordinal))
                {
                    ok = false;
                }
            }
        }
        catch (Exception exception)
        {
            ok = false;
            report.AppendLine("self-test threw: " + exception);
        }

        report.AppendLine(ok ? "SELF-TEST OK" : "SELF-TEST FAILED");

        try
        {
            Console.Out.WriteLine(report.ToString());
        }
        catch (IOException)
        {
            // No console attached (WinExe): the exit code and the log file carry the result.
        }

        try
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "self-test.log"), report.ToString());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }

        return ok ? 0 : 1;
    }
}
