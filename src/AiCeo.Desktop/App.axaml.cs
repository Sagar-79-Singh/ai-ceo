using AiCeo.Desktop.Theme;
using AiCeo.Desktop.ViewModels;
using AiCeo.Desktop.Views;
using AiCeo.Models;
using AiCeo.Services;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;

namespace AiCeo.Desktop;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            try
            {
                AppBootstrap.Initialize();

                var picker = new AvaloniaWorkspacePicker(() => desktop.MainWindow);
                var userSettings = AppBootstrap.GetRequiredService<UserSettingsService>();

                // Paint with the saved preferences before the window is created, so the chosen theme
                // is what the very first frame shows (no dark flash when light is selected).
                ApplySavedTheme(userSettings);

                var viewModel = new MainViewModel(
                    AppBootstrap.GetRequiredService<AgentDirectory>(),
                    AppBootstrap.GetRequiredService<AgentOrchestrator>(),
                    AppBootstrap.GetRequiredService<AgentHealthService>(),
                    AppBootstrap.GetRequiredService<WorkspaceService>(),
                    userSettings,
                    picker,
                    AppBootstrap.GetRequiredService<AppRuntimeInfo>(),
                    AppBootstrap.LoggerFactory.CreateLogger<MainViewModel>());

                var window = new MainWindow
                {
                    DataContext = viewModel,
                    Icon = TryLoadIcon()
                };

                desktop.MainWindow = window;

                // Follow live OS light/dark switches when the saved theme is "System".
                if (PlatformSettings is { } platformSettings)
                {
                    platformSettings.ColorValuesChanged += (_, _) => Dispatcher.UIThread.Post(() =>
                    {
                        ThemeManager.ReapplyIfFollowingSystem();
                        viewModel.OnSystemThemeChanged();
                    });
                }

                // Loading agents/workspace touches SQLite, so it runs after the window exists.
                window.Opened += async (_, _) =>
                {
                    await viewModel.InitializeAsync();

                    if (Program.SmokeTestMode)
                    {
                        await RunSmokeTestAsync(window, viewModel);
                    }
                };
            }
            catch (Exception exception)
            {
                Program.WriteCrashLog(exception, "Application startup");

                desktop.MainWindow = CreateCrashWindow(exception);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// CI smoke test: proves the window, the settings overlay and the theme pipeline all work on a
    /// real (virtual) display. Any missing marker fails the build.
    /// </summary>
    private static async Task RunSmokeTestAsync(MainWindow window, MainViewModel viewModel)
    {
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        Console.WriteLine($"UI SMOKE: window '{window.Title}' rendered, {viewModel.Agents.Count} agents loaded");

        viewModel.OpenSettingsCommand.Execute(null);
        await Task.Delay(TimeSpan.FromMilliseconds(400));

        var settings = viewModel.Settings;
        Console.WriteLine($"UI SMOKE: settings panel open = {viewModel.IsSettingsOpen}, options = {settings?.Themes.Count ?? 0} themes / {settings?.Accents.Count ?? 0} accents");

        if (settings is null)
        {
            Console.WriteLine("UI SMOKE FAILED: the settings panel did not open");
            Environment.ExitCode = 1;
            window.Close();
            return;
        }

        // Exercise the dynamic resources: switch theme + accent + text size, then confirm the
        // application resource table actually changed.
        var before = ThemeManager.Current?.Colors[UiTheme.AppBg];
        settings.SelectThemeCommand.Execute(settings.Themes.First(option => option.Value == AppTheme.Light));
        settings.SelectAccentCommand.Execute(settings.Accents.First(option => option.Value == AccentColor.Purple));
        settings.FontSizeIndex = 2;
        await Task.Delay(TimeSpan.FromMilliseconds(400));

        var after = ThemeManager.Current?.Colors[UiTheme.AppBg];
        var accent = ThemeManager.Current?.Colors[UiTheme.Accent];
        var scaled = ThemeManager.Current?.FontValues[UiTheme.FontBase];

        Console.WriteLine($"UI SMOKE: theme {before} -> {after}, accent {accent}, base font {scaled}");

        if (before == after || ThemeManager.Current?.IsDark != false || scaled is null || scaled <= 13)
        {
            Console.WriteLine("UI SMOKE FAILED: the theme did not repaint");
            Environment.ExitCode = 1;
        }

        settings.CloseCommand.Execute(null);
        await Task.Delay(TimeSpan.FromMilliseconds(250));

        if (viewModel.IsSettingsOpen)
        {
            Console.WriteLine("UI SMOKE FAILED: the settings panel did not close");
            Environment.ExitCode = 1;
        }

        window.Close();
    }

    /// <summary>Applies the stored theme, falling back to the default palette on any database issue.</summary>
    private static void ApplySavedTheme(UserSettingsService userSettings)
    {
        try
        {
            ThemeManager.Apply(userSettings.GetAsync().GetAwaiter().GetResult());
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or System.Data.Common.DbException)
        {
            // A broken database must not stop the shell from starting; InitializeAsync reports it.
            ThemeManager.Apply(UserSettingsSnapshot.Default);
        }
    }

    private static WindowIcon? TryLoadIcon()
    {
        try
        {
            var assemblyName = typeof(App).Assembly.GetName().Name ?? "AiCeo";
            using var stream = AssetLoader.Open(new Uri($"avares://{assemblyName}/Assets/aiceo.png"));
            return new WindowIcon(stream);
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or InvalidOperationException)
        {
            // The window icon is cosmetic: never fail startup over it.
            return null;
        }
    }

    /// <summary>
    /// Last-resort window shown when the UI cannot be constructed. It tells the user exactly where
    /// the crash log is instead of closing instantly.
    /// </summary>
    private static Window CreateCrashWindow(Exception exception)
    {
        var logPath = Path.Combine(AppContext.BaseDirectory, Program.CrashLogFileName);

        var text = new TextBlock
        {
            Text =
                "AI CEO Orchestrator could not start.\n\n"
                + $"The full stack trace has been written to:\n{logPath}\n\n"
                + $"Reason: {exception.GetType().Name}: {exception.Message}\n\n"
                + "If the log is not there, check the console output or run the app from a terminal.\n\n"
                + "--- first lines of the stack trace ---\n"
                + string.Join('\n', (exception.ToString() ?? string.Empty).Split('\n').Take(14)),
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12.5,
            Foreground = new SolidColorBrush(Color.Parse("#C9D6E6")),
            Margin = new Thickness(22)
        };

        return new Window
        {
            Title = "AI CEO Orchestrator — startup failed",
            Width = 780,
            Height = 460,
            Background = new SolidColorBrush(Color.Parse("#0B111A")),
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = new ScrollViewer { Content = text }
        };
    }
}
