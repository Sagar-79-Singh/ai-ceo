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

                var viewModel = new MainViewModel(
                    AppBootstrap.GetRequiredService<AgentDirectory>(),
                    AppBootstrap.GetRequiredService<AgentOrchestrator>(),
                    AppBootstrap.GetRequiredService<AgentHealthService>(),
                    AppBootstrap.GetRequiredService<WorkspaceService>(),
                    picker,
                    AppBootstrap.GetRequiredService<AppRuntimeInfo>(),
                    AppBootstrap.LoggerFactory.CreateLogger<MainViewModel>());

                var window = new MainWindow
                {
                    DataContext = viewModel,
                    Icon = TryLoadIcon()
                };

                desktop.MainWindow = window;

                // Loading agents/workspace touches SQLite, so it runs after the window exists.
                window.Opened += async (_, _) =>
                {
                    await viewModel.InitializeAsync();

                    if (Program.SmokeTestMode)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1.5));
                        Console.WriteLine($"UI SMOKE: window '{window.Title}' rendered, {viewModel.Agents.Count} agents loaded");
                        window.Close();
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
