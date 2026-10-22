namespace AiCeo.Options;

/// <summary>
/// Configuration for the local OmniRoute gateway. OmniRoute exposes an OpenAI-compatible
/// surface at <c>http://localhost:20128/v1</c>, so the official <c>OpenAI</c> .NET client is
/// pointed at that endpoint instead of api.openai.com.
/// </summary>
public sealed class OmniRouteOptions
{
    public const string SectionName = "OmniRoute";

    /// <summary>Default OmniRoute gateway base URL (see docs/RULES.md).</summary>
    public const string DefaultEndpoint = "http://localhost:20128/v1";

    /// <summary>Default model id used for new agents (see docs/UI_SPEC.md).</summary>
    public const string DefaultModel = "gh/gpt-5.4-nano";

    /// <summary>Local-only fallback key required by the gateway (see docs/RULES.md).</summary>
    public const string FallbackApiKey = "sk-48f9a7b2051199e0-64bc90-6a0ec9ee";

    public string Endpoint { get; set; } = DefaultEndpoint;

    /// <summary>Optional key; the <c>OMNIROUTE_KEY</c> environment variable always wins.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Model used by the agent editor as its default value.</summary>
    public string ModelId { get; set; } = DefaultModel;

    /// <summary>0 (default) means "let the gateway decide".</summary>
    public int MaxOutputTokens { get; set; }

    public int TimeoutSeconds { get; set; } = 300;

    public string ResolveEndpoint()
    {
        var endpoint = string.IsNullOrWhiteSpace(Endpoint) ? DefaultEndpoint : Endpoint.Trim();
        return endpoint.TrimEnd('/');
    }

    public string ResolveApiKey()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("OMNIROUTE_KEY");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment.Trim();
        }

        if (!string.IsNullOrWhiteSpace(ApiKey))
        {
            return ApiKey.Trim();
        }

        return FallbackApiKey;
    }
}
