using AiCeo.Desktop.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace AiCeo.Desktop.Views;

public partial class MainWindow : Window
{
    private MainViewModel? _subscribedModel;
    private bool _presented;

    public MainWindow()
    {
        InitializeComponent();

        Opened += (_, _) => ScrollFeedToEnd();

        // v1.0.13: show and activate the window explicitly instead of trusting the application
        // lifetime to do it — that single call is what silently stopped happening and left the app
        // running with nothing but a taskbar button.
        //
        // The call is deferred by exactly one dispatcher turn on purpose: Show() raises Opened
        // immediately, and App attaches the handler that loads the agents/workspace only after this
        // constructor has returned. Showing synchronously here would fire Opened into an empty
        // subscription and the interface would come up with no data at all. App also calls
        // PresentWindow() explicitly once the handlers are attached, so visibility never depends on
        // this post running first.
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
        _presented = WindowPresentation.Present(this, "MainWindow");
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_subscribedModel is not null)
        {
            _subscribedModel.Feed.CollectionChanged -= OnFeedChanged;
            _subscribedModel = null;
        }

        if (DataContext is MainViewModel model)
        {
            _subscribedModel = model;
            model.Feed.CollectionChanged += OnFeedChanged;
        }
    }

    private void OnFeedChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => Dispatcher.UIThread.Post(ScrollFeedToEnd, DispatcherPriority.Background);

    private void ScrollFeedToEnd()
    {
        try
        {
            FeedScroll.ScrollToEnd();
        }
        catch (Exception)
        {
            // Scrolling is cosmetic; never let it break a render.
        }
    }

    /// <summary>Ctrl+Enter sends the prompt; Enter alone keeps inserting new lines.</summary>
    private void OnComposerKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            return;
        }

        if (DataContext is MainViewModel model && model.SendCommand.CanExecute(null))
        {
            model.SendCommand.Execute(null);
        }

        e.Handled = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_subscribedModel is not null)
        {
            _subscribedModel.Feed.CollectionChanged -= OnFeedChanged;
            _subscribedModel = null;
        }

        base.OnClosed(e);
    }
}
