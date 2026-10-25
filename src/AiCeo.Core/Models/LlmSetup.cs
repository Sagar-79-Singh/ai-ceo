namespace AiCeo.Models;

/// <summary>
/// The LLM connection the user owns: provider, base URL, API key and model.
///
/// v1.0.16 removed every hardcoded key and model from the codebase. This record is what replaces
/// them: it is captured by the first-run setup screen (and editable later from Settings) and stored
/// in the <c>UserSettings</c> row of the local SQLite database. Agents carry no credentials of their
/// own unless the user explicitly gives one to a single agent, so the whole team talks to the
/// connection configured here.
/// </summary>
public sealed record LlmSetup(LlmProvider Provider, string? BaseUrl, string? ApiKey, string? ModelId)
{
    /// <summary>Nothing configured yet — the state every fresh install starts in.</summary>
    public static LlmSetup Unconfigured { get; } = new(LlmProvider.OmniRoute, null, null, null);

    public bool HasApiKey => !string.IsNullOrWhiteSpace(ApiKey);

    public bool HasModel => !string.IsNullOrWhiteSpace(ModelId);

    public bool HasBaseUrl => !string.IsNullOrWhiteSpace(BaseUrl);

    /// <summary>
    /// True only when an API key <b>and</b> a model are present — the two values v1.0.16 requires
    /// before the UI unlocks. A base URL is optional: the provider default applies when it is blank.
    /// </summary>
    public bool IsConfigured => HasApiKey && HasModel;

    /// <summary>Display name of the provider, e.g. <c>OmniRoute</c>.</summary>
    public string ProviderName => LlmProviderCatalog.DisplayNameFor(Provider);

    /// <summary>The base URL that will actually be used (explicit value or the provider default).</summary>
    public string EffectiveBaseUrl
        => HasBaseUrl ? LlmConnectionsUrl.Normalize(BaseUrl!) : LlmProviderCatalog.DefaultBaseUrlFor(Provider);

    /// <summary>Model shown in the UI; never a fabricated default.</summary>
    public string ModelLabel => HasModel ? ModelId!.Trim() : "no model configured";

    /// <summary><c>sk-…9f2a</c>, or null when no key is stored.</summary>
    public string? MaskedApiKey => Services.LlmConnections.MaskKey(ApiKey);

    /// <summary>Trims every value and drops empty strings so the stored row is unambiguous.</summary>
    public LlmSetup Normalize() => new(
        Provider,
        HasBaseUrl ? LlmConnectionsUrl.Normalize(BaseUrl!) : null,
        HasApiKey ? ApiKey!.Trim() : null,
        HasModel ? ModelId!.Trim() : null);

    /// <summary>
    /// Validates what the user typed. Throws <see cref="ArgumentException"/> with a message that is
    /// safe to show verbatim in the setup dialog.
    /// </summary>
    public LlmSetup Validate()
    {
        var normalized = Normalize();

        if (!Enum.IsDefined(normalized.Provider))
        {
            throw new ArgumentException($"Unknown provider '{normalized.Provider}'.");
        }

        if (!normalized.HasApiKey)
        {
            throw new ArgumentException(
                $"An API key is required. Paste the key issued by {normalized.ProviderName} — it is stored only in the local SQLite file.");
        }

        if (!normalized.HasModel)
        {
            throw new ArgumentException(
                "A model is required. Use the model id your provider advertises, for example the one shown in its dashboard.");
        }

        if (normalized.Provider == LlmProvider.Custom && !normalized.HasBaseUrl)
        {
            throw new ArgumentException("The Custom provider requires a base URL (for example http://localhost:11434/v1).");
        }

        if (normalized.HasBaseUrl && !LlmConnectionsUrl.IsAbsoluteHttpUrl(normalized.BaseUrl!))
        {
            throw new ArgumentException($"The base URL '{normalized.BaseUrl}' is not an absolute http:// or https:// URL.");
        }

        return normalized;
    }

    /// <summary>Human-readable one-liner for the telemetry strip and the logs.</summary>
    public string Describe()
        => IsConfigured
            ? $"{ProviderName} · {ModelLabel} · {EffectiveBaseUrl}"
            : $"{ProviderName} · not configured";
}

/// <summary>
/// The small amount of URL handling the model needs, kept here so <see cref="LlmSetup"/> does not
/// depend on the service layer. The service layer reuses it, so there is exactly one implementation.
/// </summary>
public static class LlmConnectionsUrl
{
    public static string Normalize(string url) => url.Trim().TrimEnd('/');

    public static bool IsAbsoluteHttpUrl(string url)
        => Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed)
           && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps);
}
