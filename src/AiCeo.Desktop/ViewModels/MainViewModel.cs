using System.Collections.ObjectModel;
using System.Windows.Input;
using AiCeo.Desktop.Theme;
using AiCeo.Models;
using AiCeo.Services;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;

namespace AiCeo.Desktop.ViewModels;

/// <summary>
/// The whole window: agent bench, workspace panel, live execution feed and the composer.
/// The orchestration itself lives in <see cref="AgentOrchestrator"/> (unchanged from v1.0.3).
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly AgentDirectory _directory;
    private readonly AgentOrchestrator _orchestrator;
    private readonly AgentHealthService _health;
    private readonly WorkspaceService _workspace;
    private readonly UserSettingsService _userSettings;
    private readonly IWorkspacePicker _picker;
    private readonly AppRuntimeInfo _runtime;
    private readonly ILogger<MainViewModel> _logger;

    private string _prompt = string.Empty;
    private string? _currentPrompt;
    private string? _finalOutput;
    private bool _isRunning;
    private bool _isRefreshingHealth;
    private ProviderHealth? _providerHealth;
    private WorkspaceView _workspaceView = WorkspaceView.Empty();
    private bool _toolsEnabled = true;
    private bool _suppressToolPersist;
    private AgentEditorViewModel? _editor;
    private SettingsViewModel? _settingsView;
    private string? _banner;
    private bool _bannerIsError;
    private CancellationTokenSource? _runCts;

    public MainViewModel(
        AgentDirectory directory,
        AgentOrchestrator orchestrator,
        AgentHealthService health,
        WorkspaceService workspace,
        UserSettingsService userSettings,
        IWorkspacePicker picker,
        AppRuntimeInfo runtime,
        ILogger<MainViewModel> logger)
    {
        _directory = directory;
        _orchestrator = orchestrator;
        _health = health;
        _workspace = workspace;
        _userSettings = userSettings;
        _picker = picker;
        _runtime = runtime;
        _logger = logger;

        SendCommand = new AsyncRelayCommand(SendAsync, CanSend);
        StopCommand = new RelayCommand(Stop, () => IsRunning);
        SelectWorkspaceCommand = new AsyncRelayCommand(SelectWorkspaceAsync, () => !IsRunning);
        ClearWorkspaceCommand = new AsyncRelayCommand(ClearWorkspaceAsync, () => !IsRunning && HasWorkspace);
        RefreshHealthCommand = new AsyncRelayCommand(RefreshHealthAsync, () => !IsRefreshingHealth);
        NewAgentCommand = new RelayCommand(BeginCreate, () => !IsRunning);
        OpenSettingsCommand = new RelayCommand(OpenSettings);
        CloseSettingsCommand = new RelayCommand(CloseSettings);

        Feed.CollectionChanged += OnFeedChanged;

        // Every theme change repaints the status dots and banner tints these view models own.
        ThemeManager.ThemeChanged += OnThemeChanged;
    }

    // -------------------------------------------------------------------------------- commands

    public AsyncRelayCommand SendCommand { get; }

    public RelayCommand StopCommand { get; }

    public AsyncRelayCommand SelectWorkspaceCommand { get; }

    public AsyncRelayCommand ClearWorkspaceCommand { get; }

    public AsyncRelayCommand RefreshHealthCommand { get; }

    public RelayCommand NewAgentCommand { get; }

    public RelayCommand OpenSettingsCommand { get; }

    public RelayCommand CloseSettingsCommand { get; }

    // ------------------------------------------------------------------------------- collections

    public ObservableCollection<AgentCardViewModel> Agents { get; } = new();

    public ObservableCollection<FeedItemViewModel> Feed { get; } = new();

    // ------------------------------------------------------------------------------------ state

    public string VersionLabel => $"Desktop v{_runtime.Version}";

    public string FooterText => $"db · {_runtime.DatabasePath}";

    public string CeoName => Agents.FirstOrDefault(agent => agent.IsManager)?.Name ?? "the CEO";

    public string AgentsHeader => $"AGENTS · {Agents.Count}";

    public string FlowText => $"You → {CeoName} → workers → {CeoName} → You";

    public string Prompt
    {
        get => _prompt;
        set
        {
            if (SetProperty(ref _prompt, value))
            {
                SendCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string? CurrentPrompt
    {
        get => _currentPrompt;
        private set
        {
            if (SetProperty(ref _currentPrompt, value))
            {
                OnPropertyChanged(nameof(HasPrompt));
            }
        }
    }

    public bool HasPrompt => !string.IsNullOrWhiteSpace(CurrentPrompt);

    public string? FinalOutput
    {
        get => _finalOutput;
        private set
        {
            if (SetProperty(ref _finalOutput, value))
            {
                OnPropertyChanged(nameof(HasFinalOutput));
            }
        }
    }

    public bool HasFinalOutput => !string.IsNullOrWhiteSpace(FinalOutput);

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (!SetProperty(ref _isRunning, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsNotRunning), nameof(ShowEmptyState));
            SendCommand.RaiseCanExecuteChanged();
            StopCommand.RaiseCanExecuteChanged();
            SelectWorkspaceCommand.RaiseCanExecuteChanged();
            ClearWorkspaceCommand.RaiseCanExecuteChanged();
            NewAgentCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsNotRunning => !IsRunning;

    public bool ShowEmptyState => Feed.Count == 0 && !HasPrompt && !IsRunning && !HasFinalOutput;

    public string ProviderText => _isRefreshingHealth
        ? "checking provider…"
        : _providerHealth is null
            ? "provider status"
            : $"{_providerHealth.ProviderDisplayName} · {CompactEndpoint(_providerHealth.Endpoint)}";

    public string ProviderTooltip => _providerHealth?.Message ?? "Check the provider behind the CEO";

    public IBrush ProviderDotBrush => _isRefreshingHealth
        ? ThemeBrushes.Get(UiTheme.Warn, "#FFC46B")
        : _providerHealth is null
            ? ThemeBrushes.Get(UiTheme.TextDim, "#55657A")
            : _providerHealth.Online
                ? ThemeBrushes.Get(UiTheme.Ok, "#5EE0B0")
                : ThemeBrushes.Get(UiTheme.Danger, "#FF9A9A");

    public bool IsRefreshingHealth
    {
        get => _isRefreshingHealth;
        private set
        {
            if (SetProperty(ref _isRefreshingHealth, value))
            {
                OnPropertyChanged(nameof(ProviderText), nameof(ProviderDotBrush));
                RefreshHealthCommand.RaiseCanExecuteChanged();
            }
        }
    }

    // -------------------------------------------------------------------------------- workspace

    public string WorkspacePath => _workspaceView.DisplayPath;

    public string WorkspaceStatusText => !_workspaceView.IsConfigured
        ? "not set"
        : _workspaceView.Exists ? "ready" : "missing";

    public string? WorkspaceProblem => _workspaceView.Problem;

    public bool HasWorkspaceProblem => !string.IsNullOrWhiteSpace(_workspaceView.Problem);

    public bool HasWorkspace => _workspaceView.IsConfigured;

    public bool ToolsEnabled
    {
        get => _toolsEnabled;
        set
        {
            if (!SetProperty(ref _toolsEnabled, value) || _suppressToolPersist)
            {
                return;
            }

            _ = PersistToolsAsync(value);
        }
    }

    // ----------------------------------------------------------------------------------- banner

    public string? Banner
    {
        get => _banner;
        private set
        {
            if (SetProperty(ref _banner, value))
            {
                OnPropertyChanged(nameof(HasBanner));
            }
        }
    }

    public bool HasBanner => !string.IsNullOrWhiteSpace(Banner);

    public bool BannerIsError
    {
        get => _bannerIsError;
        private set
        {
            if (SetProperty(ref _bannerIsError, value))
            {
                OnPropertyChanged(nameof(BannerBackground), nameof(BannerBorderBrush));
            }
        }
    }

    public IBrush BannerBackground => BannerIsError
        ? ThemeBrushes.Get(UiTheme.BannerErrorBg, "#2A1417")
        : ThemeBrushes.Get(UiTheme.BannerInfoBg, "#0F2430");

    public IBrush BannerBorderBrush => BannerIsError
        ? ThemeBrushes.Get(UiTheme.BannerErrorBorder, "#5A2A31")
        : ThemeBrushes.Get(UiTheme.BannerInfoBorder, "#1D4A56");

    // ----------------------------------------------------------------------------------- editor

    public AgentEditorViewModel? Editor
    {
        get => _editor;
        private set
        {
            if (SetProperty(ref _editor, value))
            {
                OnPropertyChanged(nameof(IsEditorOpen));
            }
        }
    }

    public bool IsEditorOpen => Editor is not null;

    // ----------------------------------------------------------------------------------- settings

    /// <summary>The settings panel view model; created on first open.</summary>
    public SettingsViewModel? Settings
    {
        get => _settingsView;
        private set
        {
            if (SetProperty(ref _settingsView, value))
            {
                OnPropertyChanged(nameof(IsSettingsOpen));
            }
        }
    }

    public bool IsSettingsOpen => Settings is not null;

    private void OpenSettings()
    {
        CloseEditor();

        var settings = new SettingsViewModel(_userSettings, CloseSettings);
        Settings = settings;

        // Mirror the saved values into the panel, then repaint from what was stored.
        _ = settings.LoadAsync();
    }

    private void CloseSettings() => Settings = null;

    /// <summary>Forwarded from the platform colour-scheme watcher.</summary>
    public void OnSystemThemeChanged() => Settings?.OnSystemThemeChanged();

    private void OnThemeChanged(object? sender, ThemePalette palette)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            RepaintForTheme();
        }
        else
        {
            Dispatcher.UIThread.Post(RepaintForTheme);
        }
    }

    private void RepaintForTheme()
    {
        OnPropertyChanged(nameof(ProviderDotBrush), nameof(BannerBackground), nameof(BannerBorderBrush));

        foreach (var item in Feed)
        {
            item.RefreshTheme();
        }
    }

    // ---------------------------------------------------------------------------------- startup

    /// <summary>Loads agents, the workspace and the provider badge.</summary>
    public async Task InitializeAsync()
    {
        if (!string.IsNullOrWhiteSpace(AppBootstrap.StartupWarning))
        {
            SetBanner(AppBootstrap.StartupWarning!, isError: true);
        }

        // Paint the shell with the saved preferences before the first frame is composited.
        try
        {
            ThemeManager.Apply(await _userSettings.GetAsync());
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or System.Data.Common.DbException)
        {
            _logger.LogWarning(exception, "Could not apply the saved theme; keeping the default palette");
        }

        await LoadAgentsAsync();
        await LoadWorkspaceAsync();
        await RefreshHealthAsync();
    }

    private async Task LoadAgentsAsync()
    {
        try
        {
            var views = await _directory.ListViewsAsync();

            Agents.Clear();
            foreach (var view in views)
            {
                Agents.Add(new AgentCardViewModel(view, this));
            }

            OnPropertyChanged(nameof(AgentsHeader), nameof(CeoName), nameof(FlowText));
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to load agents");
            SetBanner("Could not load agents: " + exception.Message, isError: true);
        }
    }

    private async Task LoadWorkspaceAsync()
    {
        try
        {
            _workspaceView = await _workspace.GetAsync();

            _suppressToolPersist = true;
            ToolsEnabled = _workspaceView.ToolsEnabled;
            _suppressToolPersist = false;

            OnPropertyChanged(
                nameof(WorkspacePath),
                nameof(WorkspaceStatusText),
                nameof(WorkspaceProblem),
                nameof(HasWorkspaceProblem),
                nameof(HasWorkspace));
            ClearWorkspaceCommand.RaiseCanExecuteChanged();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to load the workspace");
            SetBanner("Could not load the workspace setting: " + exception.Message, isError: true);
        }
    }

    private async Task PersistToolsAsync(bool enabled)
    {
        try
        {
            _workspaceView = await _workspace.SetToolsEnabledAsync(enabled);
            SetBanner(enabled ? "Workspace tools enabled." : "Workspace tools disabled for every agent.", isError: false);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to change the tool setting");
            SetBanner("Could not change the tool setting: " + exception.Message, isError: true);
        }
    }

    // ------------------------------------------------------------------------------- workspace UI

    private async Task SelectWorkspaceAsync()
    {
        if (IsRunning)
        {
            return;
        }

        try
        {
            var picked = await _picker.PickFolderAsync();

            if (picked.Error is not null)
            {
                SetBanner(picked.Error, isError: true);
                return;
            }

            if (!picked.Selected || string.IsNullOrWhiteSpace(picked.Path))
            {
                return;
            }

            _workspaceView = await _workspace.SetPathAsync(picked.Path);
            OnPropertyChanged(nameof(WorkspacePath), nameof(WorkspaceStatusText), nameof(WorkspaceProblem), nameof(HasWorkspaceProblem), nameof(HasWorkspace));
            ClearWorkspaceCommand.RaiseCanExecuteChanged();

            SetBanner($"Workspace set to {_workspaceView.DisplayPath}. Agents can only touch files inside this folder.", isError: false);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to set the workspace folder");
            SetBanner("Could not set the workspace: " + exception.Message, isError: true);
        }
    }

    private async Task ClearWorkspaceAsync()
    {
        if (IsRunning)
        {
            return;
        }

        try
        {
            _workspaceView = await _workspace.SetPathAsync(null);
            OnPropertyChanged(nameof(WorkspacePath), nameof(WorkspaceStatusText), nameof(WorkspaceProblem), nameof(HasWorkspaceProblem), nameof(HasWorkspace));
            ClearWorkspaceCommand.RaiseCanExecuteChanged();

            SetBanner("Workspace cleared. File and terminal tools stay off until you pick a folder.", isError: false);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to clear the workspace");
            SetBanner("Could not clear the workspace: " + exception.Message, isError: true);
        }
    }

    private async Task RefreshHealthAsync()
    {
        if (IsRefreshingHealth)
        {
            return;
        }

        IsRefreshingHealth = true;

        try
        {
            _providerHealth = await _health.CheckManagerAsync();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Provider health check failed");
            _providerHealth = new ProviderHealth(false, "Provider unavailable", "—", "—", "—");
        }
        finally
        {
            IsRefreshingHealth = false;
            OnPropertyChanged(nameof(ProviderText), nameof(ProviderTooltip), nameof(ProviderDotBrush));
        }
    }

    // ------------------------------------------------------------------------------------- agents

    internal void BeginCreate()
    {
        if (IsRunning)
        {
            return;
        }

        Editor = new AgentEditorViewModel(
            agent: null,
            LlmProviderCatalog.All,
            _workspaceView.RootPath,
            SaveAgentAsync,
            CloseEditor);
    }

    internal void BeginEdit(AgentCardViewModel card)
    {
        if (IsRunning)
        {
            return;
        }

        Editor = new AgentEditorViewModel(
            card.Agent,
            LlmProviderCatalog.All,
            _workspaceView.RootPath,
            SaveAgentAsync,
            CloseEditor);
    }

    private void CloseEditor() => Editor = null;

    private async Task SaveAgentAsync(AgentFormModel form)
    {
        if (IsRunning)
        {
            CloseEditor();
            return;
        }

        if (form.Id is { } id && id != Guid.Empty)
        {
            var updated = await _directory.UpdateAsync(id, form);

            if (updated is null)
            {
                SetBanner("That agent no longer exists.", isError: true);
                CloseEditor();
                return;
            }

            SetBanner(
                $"{updated.Name} updated · {LlmProviderCatalog.DisplayNameFor(updated.Provider)}"
                + (updated.CanUseTools ? " · workspace tools on." : "."),
                isError: false);
        }
        else
        {
            var created = await _directory.CreateAsync(form);

            SetBanner(
                $"{created.Name} added as {(created.IsManager ? "CEO" : created.Role)} · "
                + $"{LlmProviderCatalog.DisplayNameFor(created.Provider)}.",
                isError: false);
        }

        CloseEditor();
        await LoadAgentsAsync();
        await RefreshHealthAsync();
    }

    internal async Task ConfirmDeleteAsync(AgentCardViewModel card)
    {
        if (IsRunning)
        {
            return;
        }

        try
        {
            await _directory.DeleteAsync(card.Id);
            SetBanner($"{card.Name} removed.", isError: false);
            await LoadAgentsAsync();
            await RefreshHealthAsync();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to delete agent");
            SetBanner("Could not delete the agent: " + exception.Message, isError: true);
        }
    }

    internal async Task MakeCeoAsync(AgentCardViewModel card)
    {
        if (IsRunning)
        {
            return;
        }

        try
        {
            await _directory.MakeManagerAsync(card.Id);
            SetBanner($"{card.Name} is now the CEO. The user only ever talks to them.", isError: false);
            await LoadAgentsAsync();
            await RefreshHealthAsync();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to promote agent");
            SetBanner("Could not promote the agent: " + exception.Message, isError: true);
        }
    }

    // --------------------------------------------------------------------------------------- run

    private bool CanSend() => !IsRunning && !string.IsNullOrWhiteSpace(Prompt);

    private async Task SendAsync()
    {
        if (IsRunning)
        {
            return;
        }

        var text = Prompt.Trim();
        if (text.Length == 0)
        {
            return;
        }

        Prompt = string.Empty;
        CurrentPrompt = text;
        FinalOutput = null;
        Feed.Clear();
        ClearBanner();
        OnPropertyChanged(nameof(ShowEmptyState));

        _runCts = new CancellationTokenSource();
        IsRunning = true;

        try
        {
            var task = await _orchestrator.RunAsync(text, HandleLogAsync, _runCts.Token);

            FinalOutput = task.FinalOutput;
            OnPropertyChanged(nameof(HasFinalOutput), nameof(ShowEmptyState));
        }
        catch (OperationCanceledException)
        {
            SetBanner("Run cancelled. The CEO stopped before writing a final answer.", isError: false);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Run failed");
            Feed.Add(new FeedItemViewModel
            {
                Kind = FeedKind.Error,
                AgentName = CeoName,
                Text = exception.Message
            });
        }
        finally
        {
            IsRunning = false;
            OnPropertyChanged(nameof(ShowEmptyState));
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    private void OnFeedChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs args)
        => OnPropertyChanged(nameof(ShowEmptyState));

    private void Stop() => _runCts?.Cancel();

    private Task HandleLogAsync(TaskLog log)
    {
        // The orchestrator logs from background threads; the UI must be touched on the UI thread.
        Dispatcher.UIThread.Post(() => AppendLog(log));
        return Task.CompletedTask;
    }

    private void AppendLog(TaskLog log)
    {
        switch (log.Action)
        {
            case LogActions.Plan:
            {
                var steps = PlanJson.Parse(log.Content);

                Feed.Add(new FeedItemViewModel
                {
                    Kind = FeedKind.Plan,
                    AgentName = log.AgentName,
                    Text = log.Content,
                    Steps = steps
                        .Select((step, index) => new PlanStepViewModel(index + 1, step.Role, step.Instructions))
                        .ToList()
                });
                break;
            }

            case LogActions.Delegated:
                Feed.Add(new FeedItemViewModel
                {
                    Kind = FeedKind.Delegation,
                    AgentName = log.AgentName,
                    Text = log.Content,
                    IsPending = true
                });
                break;

            case LogActions.Tool:
                Feed.Add(new FeedItemViewModel
                {
                    Kind = FeedKind.ToolCall,
                    AgentName = log.AgentName,
                    ToolName = log.ToolName,
                    Text = log.Content
                });
                break;

            case LogActions.ToolResult:
                Feed.Add(new FeedItemViewModel
                {
                    Kind = FeedKind.ToolResult,
                    AgentName = log.AgentName,
                    ToolName = log.ToolName,
                    Text = log.Content,
                    Failed = log.Content.StartsWith("ERROR:", StringComparison.Ordinal)
                });
                break;

            case LogActions.Result:
                Feed.Add(new FeedItemViewModel
                {
                    Kind = FeedKind.Result,
                    AgentName = log.AgentName,
                    Text = log.Content
                });
                CompleteDelegation(log.AgentName);
                break;

            case LogActions.Error:
                Feed.Add(new FeedItemViewModel
                {
                    Kind = FeedKind.Error,
                    AgentName = log.AgentName,
                    Text = log.Content
                });
                CompleteDelegation(log.AgentName);
                break;

            default:
                Feed.Add(new FeedItemViewModel
                {
                    Kind = FeedKind.Note,
                    AgentName = log.AgentName,
                    Text = log.Content
                });
                break;
        }

        OnPropertyChanged(nameof(ShowEmptyState));
    }

    private void CompleteDelegation(string agentName)
    {
        for (var index = Feed.Count - 1; index >= 0; index--)
        {
            var item = Feed[index];

            if (item.Kind == FeedKind.Delegation
                && item.IsPending
                && string.Equals(item.AgentName, agentName, StringComparison.Ordinal))
            {
                item.IsPending = false;
                return;
            }
        }
    }

    // ------------------------------------------------------------------------------------ banner

    private void SetBanner(string message, bool isError)
    {
        BannerIsError = isError;
        Banner = message;
    }

    private void ClearBanner()
    {
        Banner = null;
        BannerIsError = false;
    }

    private static string CompactEndpoint(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || endpoint == "—")
        {
            return "—";
        }

        return Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ? uri.Host : endpoint;
    }
}
