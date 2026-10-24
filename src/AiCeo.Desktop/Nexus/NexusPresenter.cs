using System.ComponentModel;
using System.Text.Json;
using AiCeo.Desktop.Theme;
using AiCeo.Desktop.ViewModels;
using AiCeo.Models;
using AiCeo.Services;
using Avalonia.Threading;

namespace AiCeo.Desktop.Nexus;

/// <summary>
/// The bridge between the C# orchestrator and the HTML shell.
///
/// Direction 1 — C# → page: <see cref="BuildState"/> renders the whole view model (workspace,
/// agents, feed, banner, settings) as one JSON snapshot which the window evaluates as
/// <c>window.nexusReceive(…)</c>. Pushes are debounced, so a burst of feed lines costs one repaint.
///
/// Direction 2 — page → C#: <see cref="HandleMessageAsync"/> understands the handful of commands
/// the page can send (dispatch, pick workspace, edit agents, change theme …) and routes each one to
/// the same view model methods the native shell uses. No orchestration logic lives here.
/// </summary>
public sealed class NexusPresenter : IDisposable
{
    private static readonly TimeSpan PushDelay = TimeSpan.FromMilliseconds(90);

    private readonly MainViewModel _viewModel;
    private readonly UserSettingsService _settings;
    private readonly AppRuntimeInfo _runtime;
    private readonly Action<string> _dispatch;
    private readonly HashSet<FeedItemViewModel> _watchedFeedItems = new();

    private readonly DispatcherTimer _pushTimer;
    private UserSettingsSnapshot _preferences = UserSettingsSnapshot.Default;
    private bool _disposed;

    public NexusPresenter(
        MainViewModel viewModel,
        UserSettingsService settings,
        AppRuntimeInfo runtime,
        Action<string> dispatch)
    {
        _viewModel = viewModel;
        _settings = settings;
        _runtime = runtime;
        _dispatch = dispatch;

        _pushTimer = new DispatcherTimer { Interval = PushDelay };
        _pushTimer.Tick += (_, _) =>
        {
            _pushTimer.Stop();
            Push();
        };
    }

    /// <summary>Loads the saved preferences and starts streaming state to the page.</summary>
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

        _viewModel.PropertyChanged += OnViewModelChanged;
        _viewModel.Feed.CollectionChanged += OnFeedChanged;

        foreach (var item in _viewModel.Feed)
        {
            Watch(item);
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
                Tooltip: _viewModel.ProviderTooltip),
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
                    info.DefaultModel,
                    info.DefaultBaseUrl,
                    info.Hint,
                    info.RequiresApiKey))
                .ToList(),
            Footer: $"sqlite · {Path.GetFileName(_runtime.DatabasePath)}");

        return JsonSerializer.Serialize(state, NexusJson.Options);
    }

    /// <summary>Sends the current state to the page (no-op before the web view exists).</summary>
    public void Push()
    {
        if (_disposed)
        {
            return;
        }

        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(Push);
            return;
        }

        var json = BuildState();
        var literal = JsonSerializer.Serialize(json);
        _dispatch($"window.nexusReceive && window.nexusReceive({literal});");
    }

    /// <summary>Coalesces rapid changes (feed bursts, health pings) into a single repaint.</summary>
    private void SchedulePush()
    {
        if (_disposed)
        {
            return;
        }

        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(SchedulePush);
            return;
        }

        _pushTimer.Stop();
        _pushTimer.Start();
    }

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
                case "refresh":
                    Push();
                    break;

                case "selectWorkspace":
                    await GuardAsync(_viewModel.NexusPickWorkspaceAsync);
                    break;

                case "clearWorkspace":
                    await GuardAsync(_viewModel.NexusClearWorkspaceAsync);
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

        // Repaint the Avalonia shell too, so switching back to the classic UI keeps the choice.
        ThemeManager.Apply(_preferences);
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

        if (form.ModelId.Length == 0)
        {
            form.ModelId = LlmProviderCatalog.DefaultModelFor(form.Provider);
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

    private void Toast(string message, bool isError)
    {
        var literal = JsonSerializer.Serialize(message);
        _dispatch($"window.nexusToast && window.nexusToast({literal}, {(isError ? "true" : "false")});");
    }

    // ----------------------------------------------------------------------------------- mapping

    private static NexusAgentState ToState(AgentCardViewModel card) => new(
        Id: card.Id.ToString(),
        Name: card.Name,
        Initials: Initials(card.Name),
        Role: card.Role,
        IsCeo: card.IsManager,
        CanUseTools: card.CanUseTools,
        Provider: card.Agent.Provider.ToString(),
        ProviderDisplay: card.ProviderDisplayName,
        Model: card.ModelId,
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
            FeedKind.Result => "result",
            FeedKind.Error => "error",
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
            .ToList());

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
        if (args.PropertyName is nameof(MainViewModel.Prompt) or nameof(MainViewModel.IsSettingsOpen))
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

        SchedulePush();
    }

    private void Watch(FeedItemViewModel item)
    {
        if (_watchedFeedItems.Add(item))
        {
            item.PropertyChanged += OnFeedItemChanged;
        }
    }

    private void OnFeedItemChanged(object? sender, PropertyChangedEventArgs args) => SchedulePush();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pushTimer.Stop();

        _viewModel.PropertyChanged -= OnViewModelChanged;
        _viewModel.Feed.CollectionChanged -= OnFeedChanged;

        foreach (var item in _watchedFeedItems)
        {
            item.PropertyChanged -= OnFeedItemChanged;
        }

        _watchedFeedItems.Clear();
    }
}
