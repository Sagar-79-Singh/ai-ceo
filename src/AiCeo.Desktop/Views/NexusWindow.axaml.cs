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
        if (_started || _viewModel is null || _settings is null || _runtime is null)
        {
            return;
        }

        _started = true;

        try
        {
            var pageUri = NexusAssets.EnsureExtracted(out var directory);

            _presenter = new NexusPresenter(_viewModel, _settings, _runtime, ExecuteScript);

            Surface.Source = pageUri;

            await _presenter.InitializeAsync();

            Console.WriteLine($"NEXUS: surface loaded from {directory}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
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
