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
    private bool _presented;

    /// <summary>Design-time/preview constructor.</summary>
    public NexusWindow()
    {
        InitializeComponent();

        // v1.0.13: the shell shows itself. Opened is only raised by Show(), so this also guarantees
        // the WebView2 surface is initialised even if the application lifetime never shows the
        // window — the exact failure that made the app look like it had not started.
        Dispatcher.UIThread.Post(PresentWindow, DispatcherPriority.Background);
    }

    /// <summary>
    /// Forces the window on screen: normal state, in the taskbar, not topmost, shown and activated.
    /// Safe to call any number of times; only the first call does anything.
    /// </summary>
    internal void PresentWindow()
    {
        if (_presented)
        {
            return;
        }

        // Only latch on success: if the platform window could not be created yet (the very first
        // dispatcher turn), the deferred call or the lifetime's own show still gets its chance.
        _presented = WindowPresentation.Present(this, "NexusWindow");
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

    /// <summary>
    /// v1.0.13: the whole WebView2 start-up is asynchronous and never blocks the UI thread.
    ///
    /// The page extraction used to run inline on the UI thread, and every WebView2 call was made
    /// immediately afterwards — on some machines the browser process takes seconds to spin up, so
    /// the window existed but never painted. The file work now happens on the thread pool, the
    /// navigation is fire-and-await on the dispatcher, and there is not a single <c>.Result</c> or
    /// <c>.Wait()</c> anywhere on this path: awaiting is what keeps the frozen-window deadlock
    /// impossible.
    /// </summary>
    private async void OnOpened(object? sender, EventArgs e)
    {
        if (_started || _viewModel is null || _settings is null || _runtime is null)
        {
            return;
        }

        _started = true;

        try
        {
            // Show first, then talk to the browser engine: the fallback panel and the shell chrome are
            // visible immediately, whatever the runtime does next.
            PresentWindow();

            var (pageUri, directory) = await Task.Run(() =>
            {
                var uri = NexusAssets.EnsureExtracted(out var extractedTo);
                return (uri, extractedTo);
            }).ConfigureAwait(true);

            _presenter = new NexusPresenter(_viewModel, _settings, _runtime, ExecuteScript);

            Surface.Source = pageUri;

            await _presenter.InitializeAsync().ConfigureAwait(true);

            Program.Trace($"NEXUS: surface loaded from {directory}");
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
        FallbackReason.Text = $"The embedded web engine reported: {exception.Message}";
    }
}
