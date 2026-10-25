using AiCeo.Models;
using AiCeo.Options;

namespace AiCeo.Services;

/// <summary>Everything needed to reach one provider on behalf of one agent.</summary>
public sealed record LlmConnection(
    LlmProvider Provider,
    string ProviderName,
    string BaseUrl,
    Uri Endpoint,
    string ApiKey,
    string ApiKeySource,
    string ModelId,
    int TimeoutSeconds,
    int MaxOutputTokens);

/// <summary>
/// Resolves an agent's provider settings into a concrete connection: base URL defaults,
/// credential precedence, validation, and key masking for the UI.
///
/// v1.0.16 rules (there are no hardcoded keys or models any more):
/// <list type="number">
///   <item>An agent that carries <b>its own</b> API key is a self-contained connection: its
///   provider, base URL and model win. That stays available for the advanced case of one agent on
///   a different backend.</item>
///   <item>Every other agent — which is all of them on a fresh install — uses the
///   <see cref="LlmSetup"/> the user entered in the first-run setup: provider, base URL, key and
///   model all come from there.</item>
///   <item>An environment variable (<c>OMNIROUTE_KEY</c>, <c>OPENAI_API_KEY</c>, …) can still supply
///   the key when the configured one is blank, so headless and CI use keeps working.</item>
/// </list>
/// When nothing has been configured yet, resolution fails with a message that tells the user to
/// open the setup screen instead of dialling a provider with credentials baked into the build.
/// </summary>
public static class LlmConnections
{
    public const string KeySourceStored = "stored";
    public const string KeySourceSettings = "settings";
    public const string KeySourceMissing = "missing";

    /// <summary>
    /// Base URL precedence: explicit override → the user's configured base URL (for the provider it
    /// belongs to) → the OmniRoute config → the provider default.
    /// </summary>
    public static string ResolveBaseUrl(LlmProvider provider, string? agentBaseUrl, LlmSetup setup, OmniRouteOptions options)
    {
        if (!string.IsNullOrWhiteSpace(agentBaseUrl))
        {
            return NormalizeUrl(agentBaseUrl);
        }

        if (setup.Provider == provider && setup.HasBaseUrl)
        {
            return NormalizeUrl(setup.BaseUrl!);
        }

        if (provider == LlmProvider.OmniRoute && !string.IsNullOrWhiteSpace(options.Endpoint))
        {
            return NormalizeUrl(options.Endpoint);
        }

        return LlmProviderCatalog.DefaultBaseUrlFor(provider);
    }

    /// <summary>Key precedence: the agent's own key → the user's configured key → the provider's environment variable.</summary>
    public static (string? ApiKey, string Source) ResolveApiKey(
        LlmProvider provider,
        string? agentApiKey,
        LlmSetup setup,
        OmniRouteOptions options)
    {
        if (!string.IsNullOrWhiteSpace(agentApiKey))
        {
            return (agentApiKey.Trim(), KeySourceStored);
        }

        if (setup.Provider == provider && setup.HasApiKey)
        {
            return (setup.ApiKey!.Trim(), KeySourceSettings);
        }

        var info = LlmProviderCatalog.Get(provider);
        var fromEnvironment = Environment.GetEnvironmentVariable(info.ApiKeyEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return (fromEnvironment.Trim(), $"env:{info.ApiKeyEnvironmentVariable}");
        }

        if (provider == LlmProvider.OmniRoute)
        {
            var fromConfig = options.ResolveApiKey();
            if (!string.IsNullOrWhiteSpace(fromConfig))
            {
                return (fromConfig, "config:OmniRoute:ApiKey");
            }
        }

        return (null, KeySourceMissing);
    }

    /// <summary>
    /// The provider/base URL this agent will actually use. The UI reads it so the badge always
    /// matches what a run would do.
    /// </summary>
    public static (LlmProvider Provider, string BaseUrl, string ApiKeySource) Effective(
        Agent agent,
        LlmSetup setup,
        OmniRouteOptions options)
    {
        var provider = ResolveProvider(agent, setup);
        var baseUrl = ResolveBaseUrl(provider, agent.BaseUrl, setup, options);
        var (_, source) = ResolveApiKey(provider, agent.ApiKey, setup, options);

        return (provider, baseUrl, source);
    }

    /// <summary>
    /// Builds the connection or throws a human-readable <see cref="LlmException"/>.
    /// </summary>
    public static LlmConnection Resolve(Agent agent, LlmSetup setup, OmniRouteOptions options)
    {
        var provider = ResolveProvider(agent, setup);
        var info = LlmProviderCatalog.Get(provider);
        var baseUrl = ResolveBaseUrl(provider, agent.BaseUrl, setup, options);

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new LlmException(
                $"{info.DisplayName} needs a base URL. Add one in Settings → Connection "
                + "(for example http://localhost:11434/v1).");
        }

        if (!TryCreateEndpoint(baseUrl, out var endpoint))
        {
            throw new LlmException(
                $"{info.DisplayName} base URL '{baseUrl}' is not a valid absolute http(s) URL.");
        }

        var (apiKey, source) = ResolveApiKey(provider, agent.ApiKey, setup, options);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new LlmException(
                source == KeySourceMissing
                    ? $"{info.DisplayName} has no API key yet. Open the setup screen (or Settings → Connection) "
                      + "and paste your key; it is stored only in the local SQLite file."
                    : $"{info.DisplayName} requires an API key.");
        }

        var modelId = ResolveModel(agent, setup, provider);
        if (string.IsNullOrWhiteSpace(modelId))
        {
            throw new LlmException(
                $"{info.DisplayName} needs a model id. Open the setup screen (or Settings → Connection) "
                + "and enter the model you want the team to use.");
        }

        return new LlmConnection(
            provider,
            info.DisplayName,
            baseUrl,
            endpoint,
            apiKey,
            source,
            modelId,
            options.TimeoutSeconds > 0 ? options.TimeoutSeconds : 300,
            options.MaxOutputTokens);
    }

    /// <summary>Non-throwing variant used by the UI and the health probe.</summary>
    public static bool TryResolve(Agent agent, LlmSetup setup, OmniRouteOptions options, out LlmConnection? connection, out string? error)
    {
        try
        {
            connection = Resolve(agent, setup, options);
            error = null;
            return true;
        }
        catch (LlmException exception)
        {
            connection = null;
            error = exception.Message;
            return false;
        }
    }

    /// <summary>
    /// The model for this agent: its own value when it is a self-contained connection, otherwise the
    /// user's configured model, with the agent's own value as a last resort.
    /// </summary>
    public static string ResolveModel(Agent agent, LlmSetup setup, LlmProvider provider)
    {
        var agentModel = agent.ModelId?.Trim() ?? string.Empty;

        if (!string.IsNullOrEmpty(agentModel) && !string.IsNullOrWhiteSpace(agent.ApiKey))
        {
            // The agent owns its connection, so it also owns the model.
            return agentModel;
        }

        if (setup.Provider == provider && setup.HasModel)
        {
            return setup.ModelId!.Trim();
        }

        return agentModel;
    }

    /// <summary>
    /// Provider for this agent: its own when it owns a key, the user's configured provider otherwise.
    /// </summary>
    public static LlmProvider ResolveProvider(Agent agent, LlmSetup setup)
        => string.IsNullOrWhiteSpace(agent.ApiKey) && setup.HasApiKey ? setup.Provider : agent.Provider;

    /// <summary>Validates and stores an optional base URL coming from an agent form.</summary>
    public static string? NormalizeOptionalBaseUrl(LlmProvider provider, string? rawBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(rawBaseUrl))
        {
            return null;
        }

        var trimmed = NormalizeUrl(rawBaseUrl);
        if (!TryCreateEndpoint(trimmed, out _))
        {
            throw new ArgumentException("Base URL must be an absolute http:// or https:// URL.");
        }

        return trimmed;
    }

    /// <summary>Ensures a Custom agent cannot be saved without a base URL.</summary>
    public static void ValidateForSave(LlmProvider provider, string? baseUrl)
    {
        if (provider == LlmProvider.Custom && string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new ArgumentException("The Custom provider requires a Base URL.");
        }
    }

    /// <summary>Turns a secret into something safe to render: <c>sk-…9f2a</c>.</summary>
    public static string? MaskKey(string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        var key = apiKey.Trim();
        if (key.Length <= 8)
        {
            return "••••";
        }

        return $"{key[..Math.Min(4, key.Length - 4)]}…{key[^4..]}";
    }

    public static string NormalizeUrl(string url) => LlmConnectionsUrl.Normalize(url);

    public static bool TryCreateEndpoint(string baseUrl, out Uri endpoint)
    {
        if (LlmConnectionsUrl.IsAbsoluteHttpUrl(baseUrl))
        {
            endpoint = new Uri(baseUrl.Trim(), UriKind.Absolute);
            return true;
        }

        endpoint = null!;
        return false;
    }
}
