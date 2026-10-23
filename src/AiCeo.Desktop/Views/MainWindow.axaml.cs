using AiCeo.Desktop.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace AiCeo.Desktop.Views;

public partial class MainWindow : Window
{
    private MainViewModel? _subscribedModel;

    public MainWindow()
    {
        InitializeComponent();

        Opened += (_, _) => ScrollFeedToEnd();
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
