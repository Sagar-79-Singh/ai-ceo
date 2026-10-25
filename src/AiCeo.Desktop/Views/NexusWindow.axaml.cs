using AiCeo.Desktop.Nexus;
using AiCeo.Desktop.ViewModels;
using AiCeo.Models;
using AiCeo.Services;
using Avalonia.Controls;
using Avalonia.Threading;

namespace AiCeo.Desktop.Views;

/// <summary>
/// The v1.0.6 shell: a thin Avalonia window around the embedded HTML/WebGL interface.
///
/// All the pixels come from <c>Assets/nexus.html</c>; all the behaviour comes from
/// <see cref="NexusPresenter"/>, which talks to the very same <see cref="MainViewModel"/> the
/// classic window uses. Nothing about orchestration, tool sandboxing or providers changes when the
/// shell changes.
/// </summary>
public partial class NexusWindow : Window
{
    private readonly MainViewModel? _viewModel;
    private readonly UserSettingsService? _settings;
    private readonly AppRuntimeInfo? _runtime;
    private readonly Action? _openClassic;

    private NexusPresenter? _presenter;
    private bool _started;

    /// <summary>Design-time/preview constructor.</summary>
    public NexusWindow()
    {
        InitializeComponent();
    }

    public NexusWindow(
        MainViewModel viewModel,
        UserSettingsService settings,
        AppRuntimeInfo runtime,
        Action openClassic)
        : this()
    {
        _viewModel = viewModel;
        _settings = settings;
        _runtime = runtime;
        _openClassic = openClassic;

        Opened += OnOpened;
        Closed += (_, _) => _presenter?.Dispose();
    }

    /// <summary>True when the page has been handed to the web engine at least once.</summary>
    internal bool IsSurfaceReady => _presenter is not null && _started;

    private async void OnOpened(object? sender, EventArgs e)
    {
        // v1.0.11: make the window visible before anything that talks to the web engine. This is the
        // first thing that runs once the platform window exists, so no failure below — and no slow
        // WebView2 start-up — can leave the app running with a taskbar button and no window.
        WindowPresentation.PrepareForDisplay(this);

        if (_started || _viewModel is null || _settings is null || _runtime is null)
        {
            return;
        }

        _started = true;

        try
        {
            var pageUri = NexusAssets.EnsureExtracted(out var directory);

            Program.Trace($"NEXUS: page extracted to {directory}");

            _presenter = new NexusPresenter(_viewModel, _settings, _runtime, ExecuteScript);

            // Handing the URL to the adapter is what talks to WebView2 / WKWebView / WebKitGTK.
            // On a machine where that runtime is missing this throws anything from DllNotFoundException
            // to COMException, and because this handler is async void an escaping exception would kill
            // the process. Everything is caught here and turned into the in-window fallback panel.
            Surface.Source = pageUri;

            await _presenter.InitializeAsync();

            Program.Trace($"NEXUS: surface loaded from {pageUri.LocalPath}");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Program.WriteCrashLog(exception, "NEXUS surface");
            ShowFallback(exception);
        }
    }

    /// <summary>Runs a snippet inside the page (state pushes, toasts). Navigation races are ignored.</summary>
    private void ExecuteScript(string script)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await Surface.InvokeScript(script);
            }
            catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException or IOException)
            {
                // The page was not ready yet or the window is closing: the next push will catch up.
            }
        });
    }

    private async void OnWebMessageReceived(object? sender, WebMessageReceivedEventArgs e)
    {
        if (_presenter is null)
        {
            return;
        }

        try
        {
            await _presenter.HandleMessageAsync(e.Body);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Console.WriteLine("NEXUS: command failed: " + exception.Message);
        }
    }

    private void OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
    {
        // First paint: hand the page the current snapshot.
        _presenter?.Push();
    }

    private void OnClassicClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _openClassic?.Invoke();

    private void OnCloseClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

    /// <summary>Replaces the surface with an explanation when the web engine cannot start.</summary>
    private void ShowFallback(Exception exception)
    {
        Surface.IsVisible = false;
        FallbackPanel.IsVisible = true;

        var hint = OperatingSystem.IsWindows()
            ? " Install the free Microsoft Edge WebView2 Runtime (the installer offers it), or use the classic interface below."
            : " Use the classic interface below.";

        FallbackReason.Text = $"The embedded web engine reported: {exception.GetType().Name}: {exception.Message}.{hint}";

        Program.Trace("NEXUS: fallback panel shown (" + exception.GetType().Name + ")");
    }
}
