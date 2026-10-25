using System.Text.Json;

namespace AiCeo.Desktop.Nexus;

/// <summary>Everything the Nexus page needs to paint itself, in one JSON-friendly snapshot.</summary>
public sealed record NexusState(
    string Version,
    NexusWorkspaceState Workspace,
    bool ToolsEnabled,
    bool Running,
    string Prompt,
    string CeoName,
    string Flow,
    NexusProviderState Provider,
    NexusBannerState Banner,
    IReadOnlyList<NexusAgentState> Agents,
    IReadOnlyList<NexusFeedState> Feed,
    string? CurrentPrompt,
    string? FinalOutput,
    NexusSettingsState Settings,
    IReadOnlyList<NexusProviderOption> Providers,
    NexusConnectionState Connection,
    string Footer);

public sealed record NexusWorkspaceState(
    string Path,
    string RootPath,
    bool Configured,
    string Status,
    string? Problem);

public sealed record NexusProviderState(
    string Name,
    string Text,
    bool Online,
    string Tooltip,
    bool Checking = false);

public sealed record NexusBannerState(string? Text, bool IsError);

public sealed record NexusAgentState(
    string Id,
    string Name,
    string Initials,
    string Role,
    bool IsCeo,
    bool CanUseTools,
    string Provider,
    string ProviderDisplay,
    string Model,
    string Key,
    string Endpoint,
    string SystemPrompt,
    string? BaseUrl);

public sealed record NexusStepState(int Index, string Role, string Instructions);

public sealed record NexusFeedState(
    string Kind,
    string Agent,
    string Text,
    string? Tool,
    bool Failed,
    bool Pending,
    string Title,
    IReadOnlyList<NexusStepState> Steps);

public sealed record NexusSettingsState(string Theme, string Accent, string FontSize);

/// <summary>
/// The user's LLM connection, as the page sees it (v1.0.16). <see cref="Configured"/> is what drives
/// the blocking first-run setup screen; the key itself never leaves the host — only
/// <see cref="KeyMask"/> and <see cref="HasKey"/> do.
/// </summary>
public sealed record NexusConnectionState(
    bool Configured,
    string Provider,
    string ProviderDisplay,
    string BaseUrl,
    string BaseUrlPlaceholder,
    string Model,
    string KeyMask,
    bool HasKey,
    string Summary);

public sealed record NexusProviderOption(
    string Value,
    string DisplayName,
    string DefaultBaseUrl,
    string Hint,
    bool RequiresApiKey);

/// <summary>The JSON dialect spoken between <see cref="NexusPresenter"/> and the page.</summary>
public static class NexusJson
{
    /// <summary>
    /// camelCase so the JavaScript can read <c>state.workspace.path</c>. The strict encoder keeps
    /// every value inert even though v1.0.15 no longer splices the JSON into JavaScript source: the
    /// snapshot is sent as a message payload and parsed with <c>JSON.parse</c> in the page.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never
    };
}
