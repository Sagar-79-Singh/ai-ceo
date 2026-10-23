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
/// </summary>
public static class LlmConnections
{
    public const string KeySourceStored = "stored";
    public const string KeySourceMissing = "missing";
    public const string KeySourceFallback = "built-in";

    /// <summary>
    /// Base URL precedence: explicit agent override → OmniRoute config (OmniRoute only) →
    /// the provider default.
    /// </summary>
    public static string ResolveBaseUrl(LlmProvider provider, string? agentBaseUrl, OmniRouteOptions options)
    {
        if (!string.IsNullOrWhiteSpace(agentBaseUrl))
        {
            return NormalizeUrl(agentBaseUrl);
        }

        if (provider == LlmProvider.OmniRoute && !string.IsNullOrWhiteSpace(options.Endpoint))
        {
            return NormalizeUrl(options.Endpoint);
        }

        return LlmProviderCatalog.DefaultBaseUrlFor(provider);
    }

    /// <summary>
    /// Key precedence: the agent's own stored key → the provider's environment variable →
    /// (OmniRoute only) the configured key → (OmniRoute only) the local fallback key.
    /// </summary>
    public static (string? ApiKey, string Source) ResolveApiKey(
        LlmProvider provider,
        string? agentApiKey,
        OmniRouteOptions options)
    {
        if (!string.IsNullOrWhiteSpace(agentApiKey))
        {
            return (agentApiKey.Trim(), KeySourceStored);
        }

        var info = LlmProviderCatalog.Get(provider);
        var fromEnvironment = Environment.GetEnvironmentVariable(info.ApiKeyEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return (fromEnvironment.Trim(), $"env:{info.ApiKeyEnvironmentVariable}");
        }

        if (provider == LlmProvider.OmniRoute)
        {
            if (!string.IsNullOrWhiteSpace(options.ApiKey))
            {
                return (options.ApiKey.Trim(), "config:OmniRoute:ApiKey");
            }

            return (OmniRouteOptions.FallbackApiKey, KeySourceFallback);
        }

        return (null, KeySourceMissing);
    }

    /// <summary>Builds the connection or throws a human-readable <see cref="LlmException"/>.</summary>
    public static LlmConnection Resolve(Agent agent, OmniRouteOptions options)
    {
        var info = LlmProviderCatalog.Get(agent.Provider);
        var baseUrl = ResolveBaseUrl(agent.Provider, agent.BaseUrl, options);

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new LlmException(
                $"{info.DisplayName} needs a base URL. Add one to the agent (for example http://localhost:11434/v1).");
        }

        if (!TryCreateEndpoint(baseUrl, out var endpoint))
        {
            throw new LlmException(
                $"{info.DisplayName} base URL '{baseUrl}' is not a valid absolute http(s) URL.");
        }

        var (apiKey, source) = ResolveApiKey(agent.Provider, agent.ApiKey, options);
        if (string.IsNullOrWhiteSpace(apiKey) && info.RequiresApiKey)
        {
            throw new LlmException(
                $"{info.DisplayName} requires an API key. Store one on the agent or set the "
                + $"{info.ApiKeyEnvironmentVariable} environment variable.");
        }

        var modelId = string.IsNullOrWhiteSpace(agent.ModelId) ? info.DefaultModel : agent.ModelId.Trim();
        if (string.IsNullOrWhiteSpace(modelId))
        {
            throw new LlmException($"{info.DisplayName} needs a model id.");
        }

        return new LlmConnection(
            agent.Provider,
            info.DisplayName,
            baseUrl,
            endpoint,
            apiKey ?? string.Empty,
            source,
            modelId,
            options.TimeoutSeconds > 0 ? options.TimeoutSeconds : 300,
            options.MaxOutputTokens);
    }

    /// <summary>Non-throwing variant used by the UI and the REST API.</summary>
    public static bool TryResolve(Agent agent, OmniRouteOptions options, out LlmConnection? connection, out string? error)
    {
        try
        {
            connection = Resolve(agent, options);
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

    /// <summary>Validates and stores an optional base URL coming from the agent modal.</summary>
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

    public static string NormalizeUrl(string url) => url.Trim().TrimEnd('/');

    public static bool TryCreateEndpoint(string baseUrl, out Uri endpoint)
    {
        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsed)
            && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
        {
            endpoint = parsed;
            return true;
        }

        endpoint = null!;
        return false;
    }
}
