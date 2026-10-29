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
    string Footer,
    IReadOnlyList<NexusSpaceState> Spaces,
    string? ActiveSpaceId,
    bool ApprovalsEnabled,

    // v2.0.3: true for exactly as long as a tool call is parked — this is the flag the page reads to
    // open the blocking approval modal. Approval carries the tool, the command/path and the preview.
    bool PendingApproval,
    NexusApprovalState? Approval,
    IReadOnlyList<NexusThreadState> Threads,
    NexusThreadDetailState? OpenThread,
    NexusAuthState Auth,
    NexusSignInState SignIn,
    IReadOnlyList<NexusAuthOption> AuthMethods);

/// <summary>
/// v2.0.1 — one CEO ↔ worker conversation as the sidebar lists it. <see cref="Running"/> is what
/// draws the pulsing dot on a delegation that is happening right now.
/// </summary>
public sealed record NexusThreadState(
    string Id,
    string AgentName,
    string Role,
    string Status,
    string Instructions,
    int EntryCount,
    string UpdatedAt,
    bool Running);

/// <summary>v2.0.1 — one line inside a worker conversation.</summary>
public sealed record NexusThreadEntryState(
    string Kind,
    string Text,
    string? Tool,
    bool Failed,
    string At);

/// <summary>v2.0.1 — the worker detail view: who, the instruction, every step, the answer.</summary>
public sealed record NexusThreadDetailState(
    string Id,
    string AgentName,
    string Role,
    string Status,
    string Instructions,
    string Output,
    bool Running,
    IReadOnlyList<NexusThreadEntryState> Entries);

/// <summary>v2.0.1 — where the stored key came from.</summary>
public sealed record NexusAuthState(
    string Method,
    string Display,
    string? Login,
    bool SignedIn,
    string? Badge);

/// <summary>v2.0.1 — the running (or finished) device-flow sign-in, for the modal.</summary>
public sealed record NexusSignInState(
    string Method,
    string Status,
    string? UserCode,
    string? VerificationUri,
    string? Message,
    bool Active,
    string? Login)
{
    /// <summary>True when there is something to show (a code, or a result).</summary>
    public bool Visible => Status != "Idle";
}

/// <summary>v2.0.1 — one sign-in button in settings.</summary>
public sealed record NexusAuthOption(string Value, string DisplayName, string ShortName, string Description);

/// <summary>
/// v2.0.0 — a named workspace as the header dropdown sees it. <see cref="Active"/> marks the space
/// the window is showing; <see cref="FeedCount"/> is how many lines of conversation it keeps.
/// </summary>
public sealed record NexusSpaceState(
    string Id,
    string Name,
    string Path,
    bool Active,
    int FeedCount,
    string UpdatedAt);

/// <summary>
/// v2.0.0 — a tool call parked until the user presses Approve or Reject. <see cref="Preview"/> is a
/// line diff for a write and the command line for a command; nothing has run yet.
/// </summary>
public sealed record NexusApprovalState(
    string Id,
    string AgentName,
    string ToolName,
    string Target,
    string Preview,
    string Summary,
    bool IsWrite,
    string CreatedAt,
    string Label);

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

/// <summary>
/// One bubble in the chat. v2.0.1 adds <see cref="ThreadId"/>/<see cref="ThreadAgent"/> so a
/// delegation can carry an inline "Open the chat with …" button, and <see cref="HasThread"/> so the
/// page does not have to guess whether there is anything to open.
/// </summary>
public sealed record NexusFeedState(
    string Kind,
    string Agent,
    string Text,
    string? Tool,
    bool Failed,
    bool Pending,
    string Title,
    IReadOnlyList<NexusStepState> Steps,
    string? ThreadId = null,
    string? ThreadAgent = null,
    bool HasThread = false);

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
    string Summary,
    string AuthMethod = "ApiKey",
    string? AuthLogin = null,
    bool SignedIn = false);

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
