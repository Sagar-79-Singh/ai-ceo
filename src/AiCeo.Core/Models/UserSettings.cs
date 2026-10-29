namespace AiCeo.Models;

/// <summary>How the UI picks its colour scheme.</summary>
public enum AppTheme
{
    /// <summary>Follow the operating system (default).</summary>
    System = 0,

    /// <summary>Always dark.</summary>
    Dark = 1,

    /// <summary>Always light.</summary>
    Light = 2
}

/// <summary>The accent used for primary actions, badges and focus rings.</summary>
public enum AccentColor
{
    Green = 0,
    Blue = 1,
    Purple = 2,
    Amber = 3,
    Rose = 4
}

/// <summary>UI text scale.</summary>
public enum FontSizePreference
{
    Small = 0,
    Medium = 1,
    Large = 2
}

/// <summary>
/// The single row of user preferences (id 1). Stored in SQLite next to the agents so the choices
/// survive restarts.
///
/// v1.0.16 added the LLM connection (<see cref="Provider"/>, <see cref="BaseUrl"/>,
/// <see cref="ApiKey"/>, <see cref="ModelId"/>). It lives here — not in the seeded agents — because
/// there are no longer any hardcoded keys or models anywhere in the app: on a fresh install the row
/// starts empty, the first-run screen asks for these four values, and every agent uses them.
/// </summary>
public class UserSettings
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    public AppTheme Theme { get; set; } = AppTheme.System;

    public AccentColor AccentColor { get; set; } = AccentColor.Green;

    public FontSizePreference FontSize { get; set; } = FontSizePreference.Medium;

    /// <summary>Provider the whole team talks to. Set by the first-run setup screen.</summary>
    public LlmProvider Provider { get; set; } = LlmProvider.OmniRoute;

    /// <summary>Base URL for that provider; empty means "use the provider default".</summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// The user's API key. Stored in the local SQLite file only, never sent to the page or logged.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>Model id the whole team uses. Never seeded with a placeholder.</summary>
    public string? ModelId { get; set; }

    /// <summary>
    /// v2.0.1: where <see cref="ApiKey"/> came from. A device-flow sign-in stores its token in the
    /// same column (every provider wants it as a bearer token) and records the method here, so the
    /// settings screen can say "Signed in with GitHub Copilot" instead of "API key".
    /// </summary>
    public AuthMethod AuthMethod { get; set; } = AuthMethod.ApiKey;

    /// <summary>The account the device flow signed in as, when the provider told us.</summary>
    public string? AuthLogin { get; set; }

    /// <summary>When that token was stored.</summary>
    public DateTime? SignedInAt { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public LlmSetup ToSetup() => new(Provider, BaseUrl, ApiKey, ModelId);

    /// <summary>Where the key came from and who it belongs to.</summary>
    public LlmAuthState ToAuth() => new(AuthMethod, AuthLogin, SignedInAt);

    /// <summary>Records a successful device-flow sign-in.</summary>
    public void ApplyAuth(LlmAuthState auth)
    {
        AuthMethod = auth.IsDeviceFlow ? auth.Method : AuthMethod.ApiKey;
        AuthLogin = auth.IsDeviceFlow ? auth.Login : null;
        SignedInAt = auth.IsDeviceFlow ? auth.SignedInAt ?? DateTime.UtcNow : null;
        UpdatedAt = DateTime.UtcNow;
    }

    public void ApplySetup(LlmSetup setup)
    {
        var normalized = setup.Normalize();
        Provider = normalized.Provider;
        BaseUrl = normalized.BaseUrl;
        ApiKey = normalized.ApiKey;
        ModelId = normalized.ModelId;
        UpdatedAt = DateTime.UtcNow;
    }

    public UserSettingsSnapshot ToSnapshot() => new(Theme, AccentColor, FontSize, ToSetup(), ToAuth());

    public static UserSettings Defaults() => new();
}

/// <summary>Immutable, UI-friendly copy of the preferences.</summary>
public sealed record UserSettingsSnapshot(
    AppTheme Theme,
    AccentColor AccentColor,
    FontSizePreference FontSize,
    LlmSetup Setup,
    LlmAuthState Auth)
{
    /// <summary>Colour preferences only; the connection stays unconfigured.</summary>
    public UserSettingsSnapshot(AppTheme theme, AccentColor accentColor, FontSizePreference fontSize)
        : this(theme, accentColor, fontSize, LlmSetup.Unconfigured, LlmAuthState.PasteKey)
    {
    }

    public static UserSettingsSnapshot Default { get; }
        = new(AppTheme.System, AccentColor.Green, FontSizePreference.Medium, LlmSetup.Unconfigured, LlmAuthState.PasteKey);

    /// <summary>True once the first-run setup has stored a key and a model.</summary>
    public bool IsConnectionConfigured => Setup.IsConfigured;
}
