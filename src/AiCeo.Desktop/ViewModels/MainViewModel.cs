using System.Collections.ObjectModel;
using System.Windows.Input;
using AiCeo.Models;
using AiCeo.Services;
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
    private readonly SpaceService _spaces;
    private readonly ApprovalCoordinator _approvals;
    private readonly WorkerThreadStore _threads;
    private readonly WorkerThreadHub _threadHub;
    private readonly DeviceFlowCoordinator _signIn;
    private readonly LlmSetupProvider _connection;
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
    private string? _banner;
    private bool _bannerIsError;
    private CancellationTokenSource? _runCts;
    private bool _initialised;
    private IReadOnlyList<SpaceView> _spaceList = Array.Empty<SpaceView>();
    private SpaceView? _activeSpace;
    private bool _isSpaceManagerOpen;
    private IReadOnlyList<AgentThreadView> _threadList = Array.Empty<AgentThreadView>();
    private int _threadReloadPending;
    private AgentThreadDetail? _openThread;

    public MainViewModel(
        AgentDirectory directory,
        AgentOrchestrator orchestrator,
        AgentHealthService health,
        WorkspaceService workspace,
        SpaceService spaces,
        ApprovalCoordinator approvals,
        WorkerThreadStore threads,
        WorkerThreadHub threadHub,
        DeviceFlowCoordinator signIn,
        LlmSetupProvider connection,
        IWorkspacePicker picker,
        AppRuntimeInfo runtime,
        ILogger<MainViewModel> logger)
    {
        _directory = directory;
        _orchestrator = orchestrator;
        _health = health;
        _workspace = workspace;
        _spaces = spaces;
        _approvals = approvals;
        _threads = threads;
        _threadHub = threadHub;
        _signIn = signIn;
        _connection = connection;
        _picker = picker;
        _runtime = runtime;
        _logger = logger;

        SendCommand = new AsyncRelayCommand(SendAsync, CanSend);
        StopCommand = new RelayCommand(Stop, () => IsRunning);
        SelectWorkspaceCommand = new AsyncRelayCommand(SelectWorkspaceAsync, () => !IsRunning);
        ClearWorkspaceCommand = new AsyncRelayCommand(ClearWorkspaceAsync, () => !IsRunning && HasWorkspace);
        RefreshHealthCommand = new AsyncRelayCommand(RefreshHealthAsync, () => !IsRefreshingHealth);
        NewAgentCommand = new RelayCommand(BeginCreate, () => !IsRunning);
        Feed.CollectionChanged += OnFeedChanged;

        // v2.0.1: a worker thread changes on the orchestrator's thread; the presenter repaints
        // from the message loop. The hub is the only signal, so there is no polling anywhere.
        _threadHub.Changed += OnThreadChanged;

        // v2.0.1: the sign-in runs in the background. When it lands, the badge changes and the
        // provider is re-probed so the header stops saying OFFLINE without a restart.
        _signIn.Changed += OnSignInChanged;
    }

    // -------------------------------------------------------------------------------- commands

    public AsyncRelayCommand SendCommand { get; }

    public RelayCommand StopCommand { get; }

    public AsyncRelayCommand SelectWorkspaceCommand { get; }

    public AsyncRelayCommand ClearWorkspaceCommand { get; }

    public AsyncRelayCommand RefreshHealthCommand { get; }

    public RelayCommand NewAgentCommand { get; }

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

    public bool IsRefreshingHealth
    {
        get => _isRefreshingHealth;
        private set
        {
            if (SetProperty(ref _isRefreshingHealth, value))
            {
                OnPropertyChanged(nameof(ProviderText), nameof(ProviderChecking), nameof(ProviderName));
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
            SetProperty(ref _bannerIsError, value);
        }
    }

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

    // ------------------------------------------------------------------------------------ nexus

    /// <summary>Root of the selected workspace, or null when none is configured.</summary>
    public string? WorkspaceRootPath => _workspaceView.RootPath;

    /// <summary>True when the provider behind the CEO answered the last health check.</summary>
    public bool ProviderOnline => _providerHealth?.Online ?? false;

    /// <summary>True while the health check runs (or before it has ever completed).</summary>
    public bool ProviderChecking => _isRefreshingHealth || _providerHealth is null;

    /// <summary>Name of the provider behind the CEO (for the Nexus telemetry strip).</summary>
    public string ProviderName => _providerHealth?.ProviderDisplayName ?? "provider";

    /// <summary>Sends a prompt typed in the Nexus console through the normal orchestration path.</summary>
    internal Task NexusDispatchAsync(string prompt)
    {
        Prompt = prompt;
        return SendAsync();
    }

    /// <summary>Opens the native folder picker (Nexus "Select Workspace").</summary>
    internal Task NexusPickWorkspaceAsync() => SelectWorkspaceAsync();

    internal Task NexusClearWorkspaceAsync() => ClearWorkspaceAsync();

    internal Task NexusRefreshHealthAsync() => RefreshHealthAsync();

    internal Task NexusSetToolsAsync(bool enabled) => PersistToolsAsync(enabled);

    // ----------------------------------------------------------------- v2.0.0 spaces (Nexus)

    /// <summary>Every saved space, most recently used first.</summary>
    public IReadOnlyList<SpaceView> Spaces => _spaceList;

    /// <summary>The space the window is showing, or <c>null</c> when none exists yet.</summary>
    public SpaceView? ActiveSpace => _activeSpace;

    public string ActiveSpaceName => _activeSpace?.Name ?? "No space";

    public bool HasActiveSpace => _activeSpace is not null;

    /// <summary>True while the Nexus workspace manager is on screen.</summary>
    public bool IsSpaceManagerOpen
    {
        get => _isSpaceManagerOpen;
        private set => SetProperty(ref _isSpaceManagerOpen, value);
    }

    internal void NexusOpenSpaceManager() => IsSpaceManagerOpen = true;

    internal void NexusCloseSpaceManager() => IsSpaceManagerOpen = false;

    /// <summary>Creates a space in the folder that is selected right now.</summary>
    internal async Task NexusCreateSpaceAsync(string name)
    {
        if (IsRunning)
        {
            return;
        }

        await CreateSpaceCoreAsync(name, _workspaceView.RootPath);
    }

    /// <summary>Creates a space in a folder the user picks in the native dialog.</summary>
    internal async Task NexusCreateSpaceFromFolderAsync(string name)
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

            await CreateSpaceCoreAsync(name, picked.Path);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger.LogError(exception, "Failed to create a space from a folder");
            SetBanner("Could not create the space: " + exception.Message, isError: true);
        }
    }

    /// <summary>Switches the window to another space: folder first, then its conversation.</summary>
    internal async Task NexusSelectSpaceAsync(Guid id)
    {
        if (IsRunning || _activeSpace?.Id == id)
        {
            return;
        }

        try
        {
            await PersistConversationAsync();

            var activated = await _spaces.ActivateAsync(id);
            if (activated is null)
            {
                SetBanner("That space no longer exists.", isError: true);
                await ReloadSpacesAsync();
                return;
            }

            await ReloadSpacesAsync();
            await ApplySpaceAsync(activated, restoreFeed: true, refreshWorkspace: true);

            SetBanner($"Switched to \"{activated.Name}\"{DescribeFolder(activated)}.", isError: false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger.LogError(exception, "Failed to switch space");
            SetBanner("Could not switch space: " + exception.Message, isError: true);
        }
    }

    internal async Task NexusDeleteSpaceAsync(Guid id)
    {
        if (IsRunning)
        {
            return;
        }

        try
        {
            var name = _spaceList.FirstOrDefault(space => space.Id == id)?.Name ?? "space";

            if (!await _spaces.DeleteAsync(id))
            {
                await ReloadSpacesAsync();
                return;
            }

            _activeSpace = null;
            await ReloadSpacesAsync();

            var next = _spaceList.FirstOrDefault(space => space.IsActive) ?? _spaceList.FirstOrDefault();

            if (next is null)
            {
                Feed.Clear();
                CurrentPrompt = null;
                FinalOutput = null;
                OnPropertyChanged(nameof(ShowEmptyState));
                _activeSpace = null;
            }
            else
            {
                await ApplySpaceAsync(next, restoreFeed: true, refreshWorkspace: true);
            }

            SetBanner($"Space \"{name}\" deleted.", isError: false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger.LogError(exception, "Failed to delete space");
            SetBanner("Could not delete the space: " + exception.Message, isError: true);
        }
    }

    /// <summary>Saves the on-screen conversation into the active space (called after every run).</summary>
    internal async Task NexusSaveConversationAsync() => await PersistConversationAsync();

    /// <summary>Reloads the active space's conversation (used by the smoke test and on demand).</summary>
    internal async Task NexusRestoreActiveSpaceAsync() => await RestoreActiveSpaceAsync();

    // ------------------------------------------------------- v2.0.1 worker threads (Nexus)

    /// <summary>The most recent CEO ↔ worker conversations, newest first.</summary>
    public IReadOnlyList<AgentThreadView> Threads => _threadList;

    /// <summary>The conversation the detail view is showing, or null when it is closed.</summary>
    public AgentThreadDetail? OpenThread => _openThread;

    public bool IsThreadOpen => _openThread is not null;

    /// <summary>Opens one worker's conversation (from the sidebar or from a chat card).</summary>
    internal async Task NexusOpenThreadAsync(Guid id)
    {
        try
        {
            var detail = await _threads.GetAsync(id);

            if (detail is null)
            {
                SetBanner("That worker conversation is no longer stored.", isError: true);
                return;
            }

            _openThread = detail;
            OnPropertyChanged(nameof(OpenThread), nameof(IsThreadOpen));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger.LogError(exception, "Could not open worker thread {Thread}", id);
            SetBanner("Could not open the worker conversation: " + exception.Message, isError: true);
        }
    }

    /// <summary>Opens the newest conversation of a named worker.</summary>
    internal async Task NexusOpenThreadForAgentAsync(string agentName)
    {
        try
        {
            var latest = await _threads.LatestForAgentAsync(agentName);

            if (latest is null)
            {
                SetBanner($"{agentName} has not been delegated anything yet.", isError: false);
                return;
            }

            await NexusOpenThreadAsync(latest.Id);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger.LogError(exception, "Could not open the latest thread of {Agent}", agentName);
            SetBanner("Could not open the worker conversation: " + exception.Message, isError: true);
        }
    }

    /// <summary>Closes the detail view. The live thread keeps running.</summary>
    internal void NexusCloseThread()
    {
        _openThread = null;
        OnPropertyChanged(nameof(OpenThread), nameof(IsThreadOpen));
    }

    /// <summary>Reloads the thread list and, when the detail view is open, refreshes it in place.</summary>
    internal async Task NexusReloadThreadsAsync() => await ReloadThreadsAsync();

    /// <summary>The worker behind a delegation card, so the chat can link to its conversation.</summary>
    internal static string DescribeThreadHeading(AgentThreadDetail detail)
        => $"{detail.Thread.AgentName} · {detail.Thread.Role}".TrimEnd(' ', '·');

    private async Task ReloadThreadsAsync()
    {
        try
        {
            _threadList = await _threads.ListRecentAsync();
            OnPropertyChanged(nameof(Threads));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger.LogWarning(exception, "Could not list worker threads");
        }

        if (_openThread is null)
        {
            return;
        }

        try
        {
            var refreshed = await _threads.GetAsync(_openThread.Thread.Id);

            if (refreshed is not null)
            {
                _openThread = refreshed;
                OnPropertyChanged(nameof(OpenThread));
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger.LogWarning(exception, "Could not refresh the open worker thread");
        }
    }

    private void OnSignInChanged()
        => UiThread.Post(() =>
        {
            RaiseAuthChanged();
            OnPropertyChanged(nameof(ProviderText), nameof(ProviderOnline), nameof(ProviderChecking));

            if (_signIn.State.Status == DeviceFlowStatus.Complete)
            {
                SetBanner("Signed in. The token is stored locally and every agent now uses it.", isError: false);
                _ = RefreshHealthAsync();
            }
            else if (_signIn.State.Status == DeviceFlowStatus.Failed)
            {
                SetBanner(_signIn.State.Message ?? "The sign-in did not complete.", isError: true);
            }
        });

    /// <summary>
    /// v2.0.2: the hub fires once per line a worker writes — a busy delegation can produce dozens a
    /// second, and every one of them used to mean two database queries and a full repaint. Reloads
    /// are now coalesced into one every 150 ms, so the conversation stays live without the stutter.
    /// </summary>
    private void OnThreadChanged(Guid threadId)
    {
        if (Interlocked.Exchange(ref _threadReloadPending, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(150).ConfigureAwait(false);

            Volatile.Write(ref _threadReloadPending, 0);
            UiThread.Post(() => _ = NexusReloadThreadsAsync());
        });
    }

    /// <summary>
    /// The CEO's reply as a chat message. It is the final answer when there is one, and the plan
    /// summary otherwise, so the conversation always ends with the CEO speaking.
    /// </summary>
    private void AppendCeoMessage()
    {
        var text = FinalOutput;

        if (string.IsNullOrWhiteSpace(text))
        {
            var delegations = Feed
                .Where(item => item.Kind == FeedKind.Delegation)
                .Select(item => item.AgentName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            text = delegations.Count > 0
                ? $"I delegated this to {string.Join(", ", delegations)}. Their conversations are in the sidebar."
                : "The run ended without an answer.";
        }

        Feed.Add(new FeedItemViewModel
        {
            Kind = FeedKind.Ceo,
            AgentName = CeoName,
            Text = text!.Trim()
        });
    }

    // ------------------------------------------------------ v2.0.1 device-flow sign-in (Nexus)

    /// <summary>The state of the running (or last) sign-in, for the modal.</summary>
    public DeviceFlowSnapshot SignIn => _signIn.State;

    /// <summary>Where the stored key came from.</summary>
    public LlmAuthState Auth => _connection.Auth;

    /// <summary>True when the app is signed in through Copilot or ChatGPT rather than a pasted key.</summary>
    public bool IsSignedIn => _connection.IsSignedIn;

    /// <summary>A short badge for the telemetry strip: "GitHub Copilot · octocat" or null.</summary>
    public string? AuthBadge => _connection.Auth.Describe();

    /// <summary>Starts a device-flow sign-in. Returns false when one is already running.</summary>
    internal bool NexusStartSignIn(AuthMethod method)
    {
        var started = _signIn.Start(method);
        RaiseAuthChanged();
        return started;
    }

    /// <summary>Aborts the running sign-in.</summary>
    internal void NexusCancelSignIn()
    {
        _signIn.Cancel();
        _signIn.Dismiss();
        RaiseAuthChanged();
    }

    /// <summary>Closes a finished sign-in modal.</summary>
    internal void NexusDismissSignIn()
    {
        _signIn.Dismiss();
        RaiseAuthChanged();
    }

    /// <summary>Forgets a device-flow token and returns to "no key".</summary>
    internal async Task NexusSignOutAsync()
    {
        try
        {
            await _signIn.SignOutAsync();
            RaiseAuthChanged();
            SetBanner("Signed out. Add an API key or sign in again to run the swarm.", isError: false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger.LogError(exception, "Could not sign out");
            SetBanner("Could not sign out: " + exception.Message, isError: true);
        }
    }

    private void RaiseAuthChanged()
        => OnPropertyChanged(
            nameof(SignIn),
            nameof(Auth),
            nameof(IsSignedIn),
            nameof(AuthBadge));

    // -------------------------------------------------------------- v2.0.0 approvals (Nexus)

    /// <summary>True while a tool call is parked on the approval card.</summary>
    public bool HasPendingApproval => _approvals.Current is not null;

    /// <summary>Delivers the user's answer to the parked tool call.</summary>
    internal bool NexusResolveApproval(Guid id, bool approved, string? reason)
    {
        var resolved = _approvals.Resolve(id, approved, reason);

        OnPropertyChanged(nameof(HasPendingApproval));

        return resolved;
    }

    /// <summary>Arms or disarms the approval gate for this session.</summary>
    internal void NexusSetApprovalsEnabled(bool enabled)
    {
        _approvals.Enabled = enabled;
        OnPropertyChanged(nameof(HasPendingApproval));
    }

    internal AgentCardViewModel? FindAgent(Guid id) => Agents.FirstOrDefault(card => card.Id == id);

    internal Task NexusDeleteAgentAsync(AgentCardViewModel card) => ConfirmDeleteAsync(card);

    internal Task NexusMakeCeoAsync(AgentCardViewModel card) => MakeCeoAsync(card);

    /// <summary>Creates or updates an agent from the Nexus editor form.</summary>
    internal Task NexusSaveAgentAsync(AgentFormModel form) => SaveAgentAsync(form);

    /// <summary>True once the window has finished its first load; used to skip early state pushes.</summary>
    public bool IsInitialised => _initialised;

    // ---------------------------------------------------------------------------------- startup

    /// <summary>Loads agents, the workspace and the provider badge.</summary>
    public async Task InitializeAsync()
    {
        if (!string.IsNullOrWhiteSpace(AppBootstrap.StartupWarning))
        {
            SetBanner(AppBootstrap.StartupWarning!, isError: true);
        }

        await LoadAgentsAsync();
        await LoadWorkspaceAsync();

        // v2.0.0: reopen the space that was open last, with its folder and its conversation.
        await RestoreActiveSpaceAsync();

        // v2.0.1: the worker conversations the sidebar offers.
        await ReloadThreadsAsync();

        await RefreshHealthAsync();

        _initialised = true;
        OnPropertyChanged(nameof(IsInitialised));
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
            OnWorkspaceChanged();
            await SyncActiveSpaceFolderAsync(_workspaceView.RootPath);

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
            OnWorkspaceChanged();
            await SyncActiveSpaceFolderAsync(null);

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
            OnPropertyChanged(nameof(ProviderText), nameof(ProviderTooltip), nameof(ProviderChecking), nameof(ProviderName), nameof(ProviderOnline));
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

    internal async Task SaveAgentAsync(AgentFormModel form)
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

        // v2.0.1: the chat starts with what the user typed, as a right-hand bubble. The CEO's answer
        // arrives as the final message, so the transcript reads like a conversation, not a log.
        Feed.Add(new FeedItemViewModel
        {
            Kind = FeedKind.User,
            AgentName = "You",
            Text = text
        });

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

            AppendCeoMessage();
            await ReloadThreadsAsync();

            // v2.0.0: the conversation belongs to the active space and survives a restart.
            await PersistConversationAsync();

            _runCts?.Dispose();
            _runCts = null;
        }
    }

    private void OnFeedChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs args)
        => OnPropertyChanged(nameof(ShowEmptyState));

    private void Stop()
    {
        // v2.0.0: a parked tool call must not outlive the run. Answering every pending approval
        // with a rejection lets the worker wind down (and tells the CEO why) instead of hanging.
        _approvals.ResolveAll(false, "the run was stopped by the user.");
        _runCts?.Cancel();
    }

    private Task HandleLogAsync(TaskLog log)
    {
        // The orchestrator logs from background threads, and the feed is read by the presenter on
        // the message-loop thread, so every append is marshalled there (v1.0.15: UiThread, backed
        // by PhotinoX's dispatcher).
        UiThread.Post(() => AppendLog(log));
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
                    IsPending = true,

                    // v2.0.1: the conversation behind this delegation, one click away.
                    ThreadId = log.ThreadId,
                    ThreadAgent = log.AgentName
                });
                break;

            case LogActions.Tool:
                Feed.Add(new FeedItemViewModel
                {
                    Kind = FeedKind.ToolCall,
                    AgentName = log.AgentName,
                    ToolName = log.ToolName,
                    Text = log.Content,
                    ThreadId = log.ThreadId,
                    ThreadAgent = log.AgentName
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
                    Text = log.Content,
                    ThreadId = log.ThreadId,
                    ThreadAgent = log.AgentName
                });
                CompleteDelegation(log.AgentName);
                break;

            case LogActions.Approval:
                Feed.Add(new FeedItemViewModel
                {
                    Kind = FeedKind.Approval,
                    AgentName = log.AgentName,
                    ToolName = log.ToolName,
                    Text = log.Content
                });
                break;

            case LogActions.ApprovalResult:
                Feed.Add(new FeedItemViewModel
                {
                    Kind = FeedKind.Note,
                    AgentName = log.AgentName,
                    ToolName = log.ToolName,
                    Text = log.Content,
                    Failed = log.Content.StartsWith("rejected", StringComparison.OrdinalIgnoreCase)
                });
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

    // ------------------------------------------------------------------- spaces (v2.0.0 internals)

    /// <summary>
    /// Reopening the app must land the user exactly where they left: the last active space, its
    /// folder and the conversation that happened inside it.
    /// </summary>
    private async Task RestoreActiveSpaceAsync()
    {
        try
        {
            await ReloadSpacesAsync();

            var active = _spaceList.FirstOrDefault(space => space.IsActive);

            if (active is null)
            {
                _activeSpace = null;
                OnPropertyChanged(nameof(ActiveSpace), nameof(ActiveSpaceName), nameof(HasActiveSpace));
                return;
            }

            await ApplySpaceAsync(active, restoreFeed: true, refreshWorkspace: true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger.LogError(exception, "Failed to restore the active space");
            SetBanner("Could not restore the last space: " + exception.Message, isError: true);
        }
    }

    private async Task ReloadSpacesAsync()
    {
        _spaceList = await _spaces.ListAsync();

        OnPropertyChanged(nameof(Spaces), nameof(ActiveSpace), nameof(ActiveSpaceName), nameof(HasActiveSpace));
    }

    /// <summary>Points the window at a space: workspace root first, then the saved feed.</summary>
    private async Task ApplySpaceAsync(SpaceView space, bool restoreFeed, bool refreshWorkspace)
    {
        _activeSpace = space;

        if (refreshWorkspace && space.HasFolder && Directory.Exists(space.FolderPath!))
        {
            _workspaceView = await _workspace.SetPathAsync(space.FolderPath);
            _suppressToolPersist = true;
            ToolsEnabled = _workspaceView.ToolsEnabled;
            _suppressToolPersist = false;
            OnWorkspaceChanged();
        }

        OnPropertyChanged(nameof(ActiveSpace), nameof(ActiveSpaceName), nameof(HasActiveSpace));

        if (!restoreFeed)
        {
            return;
        }

        var snapshot = await _spaces.LoadConversationAsync(space.Id);

        Feed.Clear();

        if (snapshot is not null)
        {
            foreach (var item in snapshot.Feed)
            {
                Feed.Add(FromSpaceItem(item));
            }

            CurrentPrompt = string.IsNullOrWhiteSpace(snapshot.LastPrompt) ? null : snapshot.LastPrompt;
            FinalOutput = string.IsNullOrWhiteSpace(snapshot.FinalOutput) ? null : snapshot.FinalOutput;
        }
        else
        {
            CurrentPrompt = null;
            FinalOutput = null;
        }

        OnPropertyChanged(nameof(ShowEmptyState));
    }

    /// <summary>Writes the conversation currently on screen into the active space.</summary>
    private async Task PersistConversationAsync()
    {
        if (_activeSpace is null)
        {
            return;
        }

        try
        {
            await _spaces.SaveConversationAsync(
                _activeSpace.Id,
                CurrentPrompt,
                FinalOutput,
                Feed.Select(ToSpaceItem).ToList());
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Losing the history is not a reason to break the run that produced it.
            _logger.LogWarning(exception, "Could not save the conversation of space {Space}", _activeSpace.Name);
        }
    }

    private async Task CreateSpaceCoreAsync(string name, string? folderPath)
    {
        try
        {
            await PersistConversationAsync();

            var created = await _spaces.CreateAsync(name, folderPath);

            if (!string.IsNullOrWhiteSpace(folderPath))
            {
                _workspaceView = await _workspace.SetPathAsync(folderPath);
                OnWorkspaceChanged();
            }

            await ReloadSpacesAsync();
            await ApplySpaceAsync(created, restoreFeed: true, refreshWorkspace: false);

            SetBanner($"Space \"{created.Name}\" created{DescribeFolder(created)}. Its folder is the sandbox.", isError: false);
        }
        catch (ArgumentException exception)
        {
            SetBanner(exception.Message, isError: true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger.LogError(exception, "Failed to create a space");
            SetBanner("Could not create the space: " + exception.Message, isError: true);
        }
    }

    /// <summary>Keeps the active space's folder equal to the workspace the user selected.</summary>
    private async Task SyncActiveSpaceFolderAsync(string? folderPath)
    {
        if (_activeSpace is null)
        {
            return;
        }

        try
        {
            var updated = await _spaces.SetFolderAsync(_activeSpace.Id, folderPath);

            if (updated is not null)
            {
                _activeSpace = updated;
                await ReloadSpacesAsync();
            }
        }
        catch (ArgumentException exception)
        {
            SetBanner(exception.Message, isError: true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger.LogWarning(exception, "Could not update the space folder");
        }
    }

    private void OnWorkspaceChanged()
    {
        OnPropertyChanged(
            nameof(WorkspacePath),
            nameof(WorkspaceRootPath),
            nameof(WorkspaceStatusText),
            nameof(WorkspaceProblem),
            nameof(HasWorkspaceProblem),
            nameof(HasWorkspace));
        ClearWorkspaceCommand.RaiseCanExecuteChanged();
        SelectWorkspaceCommand.RaiseCanExecuteChanged();
    }

    private static string DescribeFolder(SpaceView space)
        => space.HasFolder ? $" · {space.FolderPath}" : " · no folder yet";

    /// <summary>The live feed item as it is stored in a space.</summary>
    private static SpaceFeedItem ToSpaceItem(FeedItemViewModel item) => new(
        Kind: item.Kind switch
        {
            FeedKind.Plan => "plan",
            FeedKind.Delegation => "delegation",
            FeedKind.ToolCall => "tool_call",
            FeedKind.ToolResult => "tool_result",
            FeedKind.Result => "result",
            FeedKind.Error => "error",
            FeedKind.Approval => "approval",
            FeedKind.User => "user",
            FeedKind.Ceo => "ceo",
            _ => "note"
        },
        AgentName: item.AgentName,
        Text: item.Text,
        ToolName: item.ToolName,
        Failed: item.Failed,
        Pending: item.IsPending,
        Title: item.Kind == FeedKind.Delegation ? item.DelegationTitle : item.ToolStateText,
        Steps: item.Steps
            .Select(step => new SpaceStep(step.Index, step.Role, step.Instructions))
            .ToList());

    /// <summary>A stored line as the feed renders it.</summary>
    private static FeedItemViewModel FromSpaceItem(SpaceFeedItem item) => new()
    {
        Kind = item.Kind switch
        {
            "plan" => FeedKind.Plan,
            "delegation" => FeedKind.Delegation,
            "tool_call" => FeedKind.ToolCall,
            "tool_result" => FeedKind.ToolResult,
            "result" => FeedKind.Result,
            "error" => FeedKind.Error,
            "approval" => FeedKind.Approval,
            "user" => FeedKind.User,
            "ceo" => FeedKind.Ceo,
            _ => FeedKind.Note
        },
        AgentName = item.AgentName,
        Text = item.Text,
        ToolName = item.ToolName,
        Failed = item.Failed,
        IsPending = item.Pending,
        Steps = item.Steps
            .Select(step => new PlanStepViewModel(step.Index, step.Role, step.Instructions))
            .ToList()
    };

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
