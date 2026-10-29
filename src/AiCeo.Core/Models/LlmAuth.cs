namespace AiCeo.Models;

/// <summary>
/// v2.0.1 — how the connection was authorised: a pasted API key, or a device-flow sign-in that
/// produced a token for us.
///
/// The token itself is stored in the same <c>UserSettings.ApiKey</c> column as a pasted key, because
/// every provider the app talks to expects it as a bearer token. This enum only remembers where it
/// came from, so the UI can show "Signed in with GitHub Copilot" instead of "API key".
/// </summary>
public enum AuthMethod
{
    /// <summary>A key the user typed or pasted.</summary>
    ApiKey = 0,

    /// <summary>GitHub OAuth device flow (Copilot).</summary>
    GitHubCopilot = 1,

    /// <summary>OpenAI Codex device flow (ChatGPT account).</summary>
    OpenAICodex = 2
}

/// <summary>Static facts about a device-flow sign-in, for the settings UI.</summary>
public sealed record AuthMethodInfo(AuthMethod Method, string DisplayName, string ShortName, string Description)
{
    /// <summary>Value used by the page's JSON.</summary>
    public string Value => Method.ToString();

    /// <summary>True for the two device-flow methods.</summary>
    public bool IsDeviceFlow => Method != AuthMethod.ApiKey;
}

/// <summary>Every sign-in method the settings screen offers.</summary>
public static class AuthMethodCatalog
{
    public const string GitHubClientId = "Iv1.b507a08c87ecfe98";
    public const string GitHubScope = "read:user";

    public const string OpenAiClientId = "app_EMoamEEZ73f0CkXaXp7hrann";

    public static IReadOnlyList<AuthMethodInfo> All { get; } = new[]
    {
        new AuthMethodInfo(
            AuthMethod.ApiKey,
            "API key",
            "key",
            "Paste the key your provider issued. It is stored only in the local SQLite file."),
        new AuthMethodInfo(
            AuthMethod.GitHubCopilot,
            "Sign in with GitHub Copilot",
            "GitHub",
            "Opens github.com/login/device: enter the code shown, authorise, and the token is stored "
            + "locally and used as the provider key."),
        new AuthMethodInfo(
            AuthMethod.OpenAICodex,
            "Sign in with ChatGPT (Codex)",
            "ChatGPT",
            "Opens auth.openai.com/codex/device: enter the code shown, authorise, and the token is "
            + "stored locally and used as the provider key.")
    };

    /// <summary>
    /// The model id stored when a device-flow sign-in has nothing better to offer. It is only a
    /// starting point: the settings screen shows the value and lets the user change it, and a model
    /// the user already chose is never overwritten.
    /// </summary>
    public static string SuggestedModelFor(AuthMethod method)
        => method switch
        {
            AuthMethod.GitHubCopilot => "gpt-4o",
            AuthMethod.OpenAICodex => "gpt-5-codex",
            _ => string.Empty
        };

    public static AuthMethodInfo Get(AuthMethod method)
        => All.First(info => info.Method == method);

    public static string DisplayNameFor(AuthMethod method) => Get(method).DisplayName;

    public static bool TryParse(string? value, out AuthMethod method)
    {
        if (Enum.TryParse(value, ignoreCase: true, out method) && Enum.IsDefined(method))
        {
            return true;
        }

        if (int.TryParse(value, out var number) && Enum.IsDefined(typeof(AuthMethod), number))
        {
            method = (AuthMethod)number;
            return true;
        }

        method = AuthMethod.ApiKey;
        return false;
    }
}

/// <summary>
/// Who is signed in. <see cref="Method"/> is <see cref="AuthMethod.ApiKey"/> for a pasted key, which
/// is the state every pre-2.0.1 install is in.
/// </summary>
public sealed record LlmAuthState(AuthMethod Method, string? Login, DateTime? SignedInAt)
{
    public static LlmAuthState PasteKey { get; } = new(AuthMethod.ApiKey, null, null);

    /// <summary>True when a device-flow token (not a pasted key) is in use.</summary>
    public bool IsDeviceFlow => Method != AuthMethod.ApiKey;

    public bool IsSignedIn => IsDeviceFlow && !string.IsNullOrWhiteSpace(Login);

    /// <summary>"GitHub Copilot · octocat", or null when nothing is signed in.</summary>
    public string? Describe()
    {
        if (!IsDeviceFlow)
        {
            return null;
        }

        var name = AuthMethodCatalog.DisplayNameFor(Method);

        return string.IsNullOrWhiteSpace(Login) ? name : $"{name} · {Login}";
    }
}

/// <summary>
/// What a device-flow endpoint told us to show the user. Produced by
/// <c>DeviceFlowService.StartAsync</c> and carried through the poll loop.
/// </summary>
public sealed record DeviceCodeGrant(
    AuthMethod Method,
    string DeviceCode,
    string UserCode,
    string VerificationUri,
    int ExpiresIn,
    int Interval,
    string? Extra = null)
{
    /// <summary>The code the user types, spaced for legibility (<c>ABCD-1234</c>).</summary>
    public string PrettyUserCode => UserCode.Trim();
}

/// <summary>Where the sign-in currently is. Drives the modal on the page.</summary>
public enum DeviceFlowStatus
{
    /// <summary>Nothing has been started.</summary>
    Idle = 0,

    /// <summary>A code has been issued and we are polling.</summary>
    Waiting = 1,

    /// <summary>The user authorised us; the token is stored.</summary>
    Complete = 2,

    /// <summary>The flow stopped: denied, expired, network error or cancelled.</summary>
    Failed = 3
}

/// <summary>
/// The page's view of a sign-in. <see cref="Active"/> is true while a poll loop is running.
/// </summary>
public sealed record DeviceFlowSnapshot(
    AuthMethod Method,
    DeviceFlowStatus Status,
    string? UserCode,
    string? VerificationUri,
    string? Message,
    string? Login)
{
    public static DeviceFlowSnapshot Idle { get; } = new(AuthMethod.ApiKey, DeviceFlowStatus.Idle, null, null, null, null);

    public bool Active => Status == DeviceFlowStatus.Waiting;
}

/// <summary>One poll result. <see cref="Status"/> is one of pending / slow_down / authorized / denied / expired / error.</summary>
public sealed record DeviceAuthResult(bool Success, string Status, string? AccessToken, string? Login, string? Message)
{
    public static DeviceAuthResult Pending() => new(false, "pending", null, null, null);

    public static DeviceAuthResult SlowDown() => new(false, "slow_down", null, null, null);

    public static DeviceAuthResult Authorized(string accessToken, string? login)
        => new(true, "authorized", accessToken, login, null);

    public static DeviceAuthResult Denied(string message) => new(false, "denied", null, null, message);

    public static DeviceAuthResult Expired(string message) => new(false, "expired", null, null, message);

    public static DeviceAuthResult Error(string message) => new(false, "error", null, null, message);

    public bool IsPending => !Success && (Status == "pending" || Status == "slow_down");
}
