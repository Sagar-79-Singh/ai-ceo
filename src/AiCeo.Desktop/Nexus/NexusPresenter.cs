using System.ComponentModel;
using System.Text.Json;
using AiCeo.Desktop.ViewModels;
using AiCeo.Models;
using AiCeo.Services;

namespace AiCeo.Desktop.Nexus;

/// <summary>
/// The bridge between the C# orchestrator and the HTML shell.
///
/// Direction 1 — C# → page: <see cref="BuildState"/> renders the whole view model (workspace,
/// agents, feed, banner, settings) as one JSON snapshot, which is handed to the page as
/// <c>{"type":"state","state":…}</c> through <c>PhotinoWindow.SendWebMessage</c>. Pushes are
/// debounced, so a burst of feed lines costs one repaint.
///
/// Direction 2 — page → C#: <see cref="HandleMessageAsync"/> understands the handful of commands the
/// page can send (dispatch, pick workspace, edit agents, change theme …) and routes each one to the
/// same view model methods the app has always used. No orchestration logic lives here.
///
/// v1.0.15 sends messages through PhotinoX's native web-message channel
/// (<c>PhotinoWindow.SendWebMessage</c> → <c>window.external.receiveMessage</c>) instead of
/// evaluating script in an Avalonia WebView. Nothing is string-substituted into JavaScript any
/// more: the page parses JSON.
/// </summary>
public sealed class NexusPresenter : IDisposable
{
    /// <summary>
    /// v2.0.2: one repaint per 150 ms at most. The transcript is rebuilt from a snapshot, so a burst
    /// of feed lines (a worker printing twenty tool results) used to schedule twenty serialisations;
    /// coalescing them into one is what removed the stutter while a delegation runs.
    /// </summary>
    private static readonly TimeSpan PushDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>How long the smoke test waits for the page to announce itself.</summary>
    internal static readonly TimeSpan PageReadyTimeout = TimeSpan.FromSeconds(30);

    private readonly MainViewModel _viewModel;
    private readonly UserSettingsService _settings;
    private readonly LlmSetupProvider _connection;
    private readonly ApprovalCoordinator _approvals;
    private readonly DeviceFlowCoordinator _signIn;
    private readonly AppRuntimeInfo _runtime;
    private readonly Action<string> _sendWebMessage;
    private readonly HashSet<FeedItemViewModel> _watchedFeedItems = new();
    private readonly Timer _pushTimer;
    private readonly TaskCompletionSource<bool> _pageReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private UserSettingsSnapshot _preferences = UserSettingsSnapshot.Default;
    private bool _disposed;

    /// <summary>
    /// v2.0.2: the page shows the worker chat exactly while a thread is open, so that — read live,
    /// never cached — is what decides whether a feed-only change is worth pushing. A cached copy
    /// could outlive the view it describes (a page reload, a thread closed elsewhere) and silently
    /// stop the CEO transcript from updating.
    /// </summary>
    private bool WorkerViewActive => _viewModel.IsThreadOpen;

    public NexusPresenter(
        MainViewModel viewModel,
        UserSettingsService settings,
        LlmSetupProvider connection,
        ApprovalCoordinator approvals,
        DeviceFlowCoordinator signIn,
        AppRuntimeInfo runtime,
        Action<string> sendWebMessage)
    {
        _viewModel = viewModel;
        _settings = settings;
        _connection = connection;
        _approvals = approvals;
        _signIn = signIn;
        _runtime = runtime;
        _sendWebMessage = sendWebMessage;

        // v2.0.0: the gate is armed as soon as the UI exists. Every parked write/command now waits
        // for the approval card, and cancellation or a closed window never leaves it parked.
        _approvals.Attach(_ => SchedulePush());

        // v2.0.1: a device-flow sign-in is started from the page and finishes on the thread pool;
        // every state change repaints the modal.
        _signIn.Changed += OnSignInStateChanged;

        // A thread-pool timer rather than a UI-thread DispatcherTimer: the tick only schedules a
        // push, and Push() marshals itself onto the message loop. Nothing waits on the UI thread.
        _pushTimer = new Timer(_ => Push(), state: null, Timeout.Infinite, Timeout.Infinite);

        _viewModel.PropertyChanged += OnViewModelChanged;
        _viewModel.Feed.CollectionChanged += OnFeedChanged;

        foreach (var item in _viewModel.Feed)
        {
            Watch(item);
        }
    }

    /// <summary>True once the page has sent its first <c>ready</c> command.</summary>
    internal bool IsPageReady => _pageReady.Task.IsCompletedSuccessfully;

    /// <summary>Completes when the page announces itself, or when the timeout elapses.</summary>
    internal Task<bool> WaitForPageAsync(CancellationToken cancellationToken = default)
        => _pageReady.Task.WaitAsync(PageReadyTimeout, cancellationToken);

    /// <summary>
    /// Loads the saved preferences and pushes the first snapshot. Called from the background
    /// start-up task once the native window exists, so the window is on screen before any database
    /// work starts — the start-up stall this release removes.
    /// </summary>
    public async Task InitializeAsync()
    {
        try
        {
            _preferences = await _settings.GetAsync();
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or System.Data.Common.DbException)
        {
            _preferences = UserSettingsSnapshot.Default;
        }

        Push();
    }

    /// <summary>Serialises everything the page needs to paint itself.</summary>
    public string BuildState()
    {
        var state = new NexusState(
            Version: _runtime.Version,
            Workspace: new NexusWorkspaceState(
                Path: _viewModel.WorkspacePath,
                RootPath: _viewModel.WorkspaceRootPath ?? string.Empty,
                Configured: _viewModel.HasWorkspace,
                Status: _viewModel.WorkspaceStatusText,
                Problem: _viewModel.HasWorkspaceProblem ? _viewModel.WorkspaceProblem : null),
            ToolsEnabled: _viewModel.ToolsEnabled,
            Running: _viewModel.IsRunning,
            Prompt: _viewModel.Prompt,
            CeoName: _viewModel.CeoName,
            Flow: _viewModel.FlowText,
            Provider: new NexusProviderState(
                Name: _viewModel.ProviderName,
                Text: _viewModel.ProviderText,
                Online: _viewModel.ProviderOnline,
                Tooltip: _viewModel.ProviderTooltip,
                Checking: _viewModel.ProviderChecking),
            Banner: new NexusBannerState(_viewModel.Banner, _viewModel.BannerIsError),
            Agents: _viewModel.Agents.Select(ToState).ToList(),
            Feed: _viewModel.Feed.Select(ToState).ToList(),
            CurrentPrompt: _viewModel.CurrentPrompt,
            FinalOutput: _viewModel.FinalOutput,
            Settings: new NexusSettingsState(
                _preferences.Theme.ToString(),
                _preferences.AccentColor.ToString(),
                _preferences.FontSize.ToString()),
            Providers: LlmProviderCatalog.All
                .Select(info => new NexusProviderOption(
                    info.Value,
                    info.DisplayName,
                    info.DefaultBaseUrl,
                    info.Hint,
                    info.RequiresApiKey))
                .ToList(),
            Connection: ToState(_connection.Current, _connection.Auth),
            Footer: $"sqlite · {Path.GetFileName(_runtime.DatabasePath)}",
            Spaces: _viewModel.Spaces.Select(ToState).ToList(),
            ActiveSpaceId: _viewModel.ActiveSpace?.Id.ToString(),
            ApprovalsEnabled: _approvals.Enabled,
            Approval: ToState(_approvals.Current),
            Threads: _viewModel.Threads.Select(ToState).ToList(),
            OpenThread: ToState(_viewModel.OpenThread),
            Auth: new NexusAuthState(
                Method: _viewModel.Auth.Method.ToString(),
                Display: AuthMethodCatalog.DisplayNameFor(_viewModel.Auth.Method),
                Login: _viewModel.Auth.Login,
                SignedIn: _viewModel.IsSignedIn,
                Badge: _viewModel.AuthBadge),
            SignIn: ToState(_viewModel.SignIn),
            AuthMethods: AuthMethodCatalog.All
                .Where(info => info.IsDeviceFlow)
                .Select(info => new NexusAuthOption(
                    info.Value,
                    info.DisplayName,
                    info.ShortName,
                    info.Description))
                .ToList());

        return JsonSerializer.Serialize(state, NexusJson.Options);
    }

    /// <summary>Sends the current state to the page (no-op before the web view exists).</summary>
    public void Push()
    {
        if (_disposed)
        {
            return;
        }

        if (!UiThread.CheckAccess())
        {
            UiThread.Post(Push);
            return;
        }

        // The snapshot is embedded as raw JSON, so the page receives one message and parses it
        // once. Nothing is concatenated into JavaScript source.
        Send("{\"type\":\"state\",\"state\":" + BuildState() + "}");
    }

    /// <summary>Coalesces rapid changes (feed bursts, health pings) into a single repaint.</summary>
    /// <param name="feedOnly">
    /// True when this change only affects the CEO transcript (a feed line, a delegation's pending
    /// flag). Such a repaint is pointless while a worker chat is on screen, so it is dropped.
    /// </param>
    private void SchedulePush(bool feedOnly = false)
    {
        if (_disposed)
        {
            return;
        }

        if (feedOnly && WorkerViewActive)
        {
            // The transcript moved on, but nobody can see it. The snapshot (and the whole feed) is
            // sent when the user goes back to the CEO chat.
            return;
        }

        try
        {
            _pushTimer.Change(PushDelay, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Shutting down.
        }
    }

    /// <summary>A device-flow sign-in changed state: repaint the modal (coalesced).</summary>
    private void OnSignInStateChanged() => SchedulePush();

    /// <summary>
    /// Handles one JSON command from the page. Unknown types are ignored on purpose: the page can
    /// be edited without breaking the host.
    /// </summary>
    public async Task HandleMessageAsync(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(message);
        }
        catch (JsonException)
        {
            return;
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var typeElement)
                || typeElement.ValueKind != JsonValueKind.String)
            {
                return;
            }

            var type = typeElement.GetString() ?? string.Empty;

            switch (type)
            {
                case "ready":
                    _pageReady.TrySetResult(true);
                    Push();
                    break;

                case "refresh":
                    Push();
                    break;

                case "selectWorkspace":
                    await GuardAsync(_viewModel.NexusPickWorkspaceAsync);
                    break;

                case "clearWorkspace":
                    await GuardAsync(_viewModel.NexusClearWorkspaceAsync);
                    break;

                case "selectSpace":
                    if (Guid.TryParse(ReadString(root, "id"), out var spaceId))
                    {
                        await GuardAsync(() => _viewModel.NexusSelectSpaceAsync(spaceId));
                    }

                    break;

                case "createSpace":
                    await GuardAsync(() => _viewModel.NexusCreateSpaceAsync(ReadString(root, "name")));
                    break;

                case "createSpaceWithFolder":
                    await GuardAsync(() => _viewModel.NexusCreateSpaceFromFolderAsync(ReadString(root, "name")));
                    break;

                case "deleteSpace":
                    if (Guid.TryParse(ReadString(root, "id"), out var doomed))
                    {
                        await GuardAsync(() => _viewModel.NexusDeleteSpaceAsync(doomed));
                    }

                    break;

                case "approval":
                    if (Guid.TryParse(ReadString(root, "id"), out var approvalId))
                    {
                        ResolveApproval(approvalId, ReadBool(root, "approved", false), ReadString(root, "reason"));
                    }

                    break;

                case "openThread":
                    if (Guid.TryParse(ReadString(root, "id"), out var threadId))
                    {
                        await GuardAsync(() => _viewModel.NexusOpenThreadAsync(threadId));
                        Push();
                    }

                    break;

                case "openAgentThread":
                    await GuardAsync(() => _viewModel.NexusOpenThreadForAgentAsync(ReadString(root, "agent")));
                    Push();
                    break;

                case "closeThread":
                    _viewModel.NexusCloseThread();
                    Push();
                    break;

                case "signIn":
                    if (AuthMethodCatalog.TryParse(ReadString(root, "method"), out var authMethod))
                    {
                        if (!_viewModel.NexusStartSignIn(authMethod))
                        {
                            Toast("A sign-in is already running. Finish or cancel it first.", isError: true);
                        }
                    }

                    Push();
                    break;

                case "cancelSignIn":
                    _viewModel.NexusCancelSignIn();
                    Push();
                    break;

                case "dismissSignIn":
                    _viewModel.NexusDismissSignIn();
                    Push();
                    break;

                case "openSignInPage":
                    // The modal shows the code; this saves the user from retyping the URL.
                    if (!LinkLauncher.Open(_signIn.State.VerificationUri))
                    {
                        Toast("Could not open a browser. Copy the link shown in the sign-in window.", isError: true);
                    }

                    break;

                case "signOut":
                    await GuardAsync(_viewModel.NexusSignOutAsync);
                    await GuardAsync(_viewModel.NexusRefreshHealthAsync);
                    Push();
                    break;

                case "setApprovals":
                    _viewModel.NexusSetApprovalsEnabled(ReadBool(root, "enabled", true));
                    break;

                case "refreshHealth":
                    await GuardAsync(_viewModel.NexusRefreshHealthAsync);
                    break;

                case "setTools":
                    await GuardAsync(() => _viewModel.NexusSetToolsAsync(ReadBool(root, "enabled", true)));
                    break;

                case "dispatch":
                    await GuardAsync(() => _viewModel.NexusDispatchAsync(ReadString(root, "prompt").Trim()));
                    break;

                case "stop":
                    _viewModel.StopCommand.Execute(null);
                    break;

                case "settings":
                    await ApplySettingsAsync(root);
                    break;

                case "saveConnection":
                    await SaveConnectionAsync(root);
                    break;

                case "saveAgent":
                    await SaveAgentAsync(root);
                    break;

                case "deleteAgent":
                    await DeleteAgentAsync(root);
                    break;

                case "makeCeo":
                    await MakeCeoAsync(root);
                    break;

                default:
                    return;
            }
        }

        Push();
    }

    /// <summary>
    /// Entry point for the native web-message handler. Runs on the message-loop thread and never
    /// lets an exception escape into the native callback.
    /// </summary>
    internal async void Accept(string? message)
    {
        if (!UiThread.CheckAccess())
        {
            UiThread.Post(() => Accept(message));
            return;
        }

        try
        {
            await HandleMessageAsync(message);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Program.WriteCrashLog(exception, "Nexus command");
            Console.WriteLine("NEXUS: command failed: " + exception.Message);
        }
    }

    private async Task GuardAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Toast(exception.Message, isError: true);
        }
    }

    private async Task ApplySettingsAsync(JsonElement root)
    {
        var theme = ParseEnum(ReadString(root, "theme"), _preferences.Theme);
        var accent = ParseEnum(ReadString(root, "accent"), _preferences.AccentColor);
        var fontSize = ParseEnum(ReadString(root, "fontSize"), _preferences.FontSize);

        _preferences = new UserSettingsSnapshot(theme, accent, fontSize);

        // The page repaints itself from these values; the host only stores them.
        Push();

        try
        {
            _preferences = await _settings.SaveAsync(_preferences);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or System.Data.Common.DbException)
        {
            Toast("Could not save the theme: " + exception.Message, isError: true);
        }

        Push();
    }

    /// <summary>
    /// Stores the connection the user typed in the first-run setup (or in Settings → Connection).
    /// The values are validated in Core, persisted in the <c>UserSettings</c> row and published
    /// immediately, so the next agent run uses them without a restart.
    /// </summary>
    private async Task SaveConnectionAsync(JsonElement root)
    {
        var providerText = ReadString(root, "provider");

        // A blank key means "keep the stored one": the editor shows a masked key, never the secret,
        // so the field is empty unless the user is deliberately replacing it.
        var apiKey = ReadString(root, "apiKey");
        if (string.IsNullOrWhiteSpace(apiKey) && _connection.Current.HasApiKey)
        {
            apiKey = _connection.Current.ApiKey!;
        }

        var form = new LlmSetup(
            LlmProviderCatalog.TryParse(providerText, out var provider) ? provider : LlmProvider.OmniRoute,
            NullIfBlank(ReadString(root, "baseUrl")),
            apiKey,
            ReadString(root, "modelId"));

        try
        {
            var saved = await _connection.SaveAsync(form);
            Toast($"{saved.ProviderName} connected · model {saved.ModelLabel}", isError: false);

            // Re-probe the new endpoint so the telemetry strip stops saying CHECKING/OFFLINE.
            await GuardAsync(_viewModel.NexusRefreshHealthAsync);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            Toast(exception.Message, isError: true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Program.WriteCrashLog(exception, "saving the LLM connection");
            Toast("Could not save the connection: " + exception.Message, isError: true);
        }
    }

    private async Task SaveAgentAsync(JsonElement root)
    {
        var idText = ReadString(root, "id");
        var providerText = ReadString(root, "provider");

        var form = new AgentFormModel
        {
            Id = Guid.TryParse(idText, out var id) && id != Guid.Empty ? id : null,
            Name = ReadString(root, "name").Trim(),
            Role = ReadString(root, "role").Trim(),
            SystemPrompt = ReadString(root, "systemPrompt"),
            ModelId = ReadString(root, "modelId").Trim(),
            Provider = LlmProviderCatalog.TryParse(providerText, out var provider) ? provider : LlmProvider.OmniRoute,
            BaseUrl = NullIfBlank(ReadString(root, "baseUrl")),
            ApiKey = NullIfBlank(ReadString(root, "apiKey")),
            CanUseTools = ReadBool(root, "canUseTools", true)
        };

        if (form.Name.Length == 0 || form.Role.Length == 0)
        {
            Toast("An agent needs a name and a role.", isError: true);
            return;
        }

        await GuardAsync(() => _viewModel.NexusSaveAgentAsync(form));
    }

    private async Task DeleteAgentAsync(JsonElement root)
    {
        if (!Guid.TryParse(ReadString(root, "id"), out var id) || _viewModel.FindAgent(id) is not { } card)
        {
            return;
        }

        await GuardAsync(() => _viewModel.NexusDeleteAgentAsync(card));
    }

    private async Task MakeCeoAsync(JsonElement root)
    {
        if (!Guid.TryParse(ReadString(root, "id"), out var id) || _viewModel.FindAgent(id) is not { } card)
        {
            return;
        }

        await GuardAsync(() => _viewModel.NexusMakeCeoAsync(card));
    }

    /// <summary>
    /// Feeds the user's answer back into the parked orchestrator thread. A rejection without a typed
    /// reason still carries one, so the CEO always knows why its worker was stopped.
    /// </summary>
    private void ResolveApproval(Guid id, bool approved, string reason)
    {
        if (!_viewModel.NexusResolveApproval(id, approved, reason))
        {
            // Nothing is waiting any more (the run was stopped, or the page double-clicked).
            return;
        }

        Toast(approved ? "Approved — the worker continues." : "Rejected — the CEO will plan around it.", isError: false);
    }

    private void Toast(string message, bool isError)
    {
        var text = JsonSerializer.Serialize(message);
        Send($"{{\"type\":\"toast\",\"text\":{text},\"isError\":{(isError ? "true" : "false")}}}");
    }

    private void Send(string message)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _sendWebMessage(message);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException or IOException)
        {
            // The engine has not initialised yet or the window is closing: the next push catches up.
        }
    }

    // ----------------------------------------------------------------------------------- mapping

    private static NexusConnectionState ToState(LlmSetup setup, LlmAuthState auth)
    {
        var provider = LlmProviderCatalog.Get(setup.Provider);

        // A device-flow token reads as "GitHub Copilot · octocat" instead of a bare key.
        var summary = auth.Describe();

        return new NexusConnectionState(
            Configured: setup.IsConfigured,
            Provider: setup.Provider.ToString(),
            ProviderDisplay: setup.ProviderName,
            BaseUrl: setup.BaseUrl ?? string.Empty,
            BaseUrlPlaceholder: provider.DefaultBaseUrl,
            Model: setup.ModelId ?? string.Empty,
            KeyMask: setup.MaskedApiKey ?? string.Empty,
            HasKey: setup.HasApiKey,
            Summary: summary is null ? setup.Describe() : $"{setup.ProviderName} · {summary}",
            AuthMethod: auth.Method.ToString(),
            AuthLogin: auth.Login,
            SignedIn: auth.IsDeviceFlow);
    }

    private static NexusThreadState ToState(AgentThreadView thread) => new(
        Id: thread.Id.ToString(),
        AgentName: thread.AgentName,
        Role: thread.Role,
        Status: thread.Status,
        Instructions: thread.Instructions,
        EntryCount: thread.EntryCount,
        UpdatedAt: thread.UpdatedAt.ToLocalTime().ToString("HH:mm"),
        Running: thread.IsRunning);

    private static NexusThreadDetailState? ToState(AgentThreadDetail? detail) => detail is null
        ? null
        : new NexusThreadDetailState(
            Id: detail.Thread.Id.ToString(),
            AgentName: detail.Thread.AgentName,
            Role: detail.Thread.Role,
            Status: detail.Thread.Status,
            Instructions: detail.Thread.Instructions,
            Output: detail.Output,
            Running: detail.Thread.IsRunning,
            Entries: detail.Entries
                .Select(entry => new NexusThreadEntryState(
                    entry.Kind,
                    entry.Text,
                    entry.ToolName,
                    entry.Failed,
                    entry.At))
                .ToList());

    private static NexusSignInState ToState(DeviceFlowSnapshot signIn) => new(
        Method: signIn.Method.ToString(),
        Status: signIn.Status.ToString(),
        UserCode: signIn.UserCode,
        VerificationUri: signIn.VerificationUri,
        Message: signIn.Message,
        Active: signIn.Active,
        Login: signIn.Login);

    private static NexusSpaceState ToState(SpaceView space) => new(
        Id: space.Id.ToString(),
        Name: space.Name,
        Path: space.FolderPath ?? string.Empty,
        Active: space.IsActive,
        FeedCount: space.FeedCount,
        UpdatedAt: space.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));

    private static NexusApprovalState? ToState(PendingApproval? approval) => approval is null
        ? null
        : new NexusApprovalState(
            Id: approval.Id.ToString(),
            AgentName: approval.AgentName,
            ToolName: approval.ToolName,
            Target: approval.Target,
            Preview: approval.Preview,
            Summary: approval.Summary,
            IsWrite: approval.IsWrite,
            CreatedAt: approval.CreatedAt.ToLocalTime().ToString("HH:mm:ss"),
            Label: approval.IsWrite ? "WRITE" : "COMMAND");

    private static NexusAgentState ToState(AgentCardViewModel card) => new(
        Id: card.Id.ToString(),
        Name: card.Name,
        Initials: Initials(card.Name),
        Role: card.Role,
        IsCeo: card.IsManager,
        CanUseTools: card.CanUseTools,
        Provider: card.Agent.Provider.ToString(),
        ProviderDisplay: card.ProviderDisplayName,
        Model: card.OwnModelId,
        Key: card.KeyText,
        Endpoint: card.EndpointText,
        SystemPrompt: card.Agent.SystemPrompt,
        BaseUrl: card.Agent.BaseUrl);

    private static NexusFeedState ToState(FeedItemViewModel item) => new(
        Kind: item.Kind switch
        {
            FeedKind.Plan => "plan",
            FeedKind.Delegation => "delegation",
            FeedKind.ToolCall or FeedKind.ToolResult => "tool",
            FeedKind.Approval => "approval",
            FeedKind.Result => "result",
            FeedKind.Error => "error",
            FeedKind.User => "user",
            FeedKind.Ceo => "ceo",
            _ => "note"
        },
        Agent: item.AgentName,
        Text: item.Text,
        Tool: item.ToolName,
        Failed: item.Failed,
        Pending: item.IsPending,
        Title: item.Kind == FeedKind.Delegation ? item.DelegationTitle : item.ToolStateText,
        Steps: item.Steps
            .Select(step => new NexusStepState(step.Index, step.Role, step.Instructions))
            .ToList(),
        ThreadId: item.ThreadId?.ToString(),
        ThreadAgent: item.ThreadAgent,
        HasThread: item.ThreadId is not null);

    private static string Initials(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return words.Length switch
        {
            0 => "??",
            1 => words[0].Length >= 2 ? words[0][..2].ToUpperInvariant() : words[0].ToUpperInvariant(),
            _ => string.Concat(char.ToUpperInvariant(words[0][0]), char.ToUpperInvariant(words[^1][0]))
        };
    }

    private static string ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static bool ReadBool(JsonElement root, string name, bool fallback)
        => root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;

    private static TEnum ParseEnum<TEnum>(string value, TEnum fallback)
        where TEnum : struct, Enum
        => Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) ? parsed : fallback;

    private static string? NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // ---------------------------------------------------------------------------------- plumbing

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        // The composer keeps its own text; echoing it back on every keystroke would fight the caret.
        if (args.PropertyName is nameof(MainViewModel.Prompt))
        {
            return;
        }

        SchedulePush();
    }

    private void OnFeedChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs args)
    {
        foreach (var item in _viewModel.Feed)
        {
            Watch(item);
        }

        SchedulePush(feedOnly: true);
    }

    private void Watch(FeedItemViewModel item)
    {
        if (_watchedFeedItems.Add(item))
        {
            item.PropertyChanged += OnFeedItemChanged;
        }
    }

    private void OnFeedItemChanged(object? sender, PropertyChangedEventArgs args) => SchedulePush(feedOnly: true);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _pushTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _pushTimer.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // Already gone.
        }

        _viewModel.PropertyChanged -= OnViewModelChanged;
        _viewModel.Feed.CollectionChanged -= OnFeedChanged;

        foreach (var item in _watchedFeedItems)
        {
            item.PropertyChanged -= OnFeedItemChanged;
        }

        _watchedFeedItems.Clear();
        _pageReady.TrySetCanceled();

        // v2.0.0: nothing may stay parked on a decision once the window is gone.
        _approvals.ResolveAll(false, "the window was closed.");

        // v2.0.1: and no device flow may keep polling for a page that no longer exists.
        _signIn.Changed -= OnSignInStateChanged;
        _signIn.Cancel();
    }
}
