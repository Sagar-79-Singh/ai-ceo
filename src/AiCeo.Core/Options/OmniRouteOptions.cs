namespace AiCeo.Options;

/// <summary>
/// Configuration for the local OmniRoute gateway. OmniRoute exposes an OpenAI-compatible
/// surface at <c>http://localhost:20128/v1</c>, so the official <c>OpenAI</c> .NET client is
/// pointed at that endpoint instead of api.openai.com.
/// </summary>
public sealed class OmniRouteOptions
{
    public const string SectionName = "OmniRoute";

    /// <summary>Default OmniRoute gateway base URL.</summary>
    public const string DefaultEndpoint = "http://localhost:20128/v1";

    public string Endpoint { get; set; } = DefaultEndpoint;

    /// <summary>Optional key; the <c>OMNIROUTE_KEY</c> environment variable always wins.</summary>
    public string? ApiKey { get; set; }

    /// <summary>0 (default) means "let the gateway decide".</summary>
    public int MaxOutputTokens { get; set; }

    public int TimeoutSeconds { get; set; } = 300;

    public string ResolveEndpoint()
    {
        var endpoint = string.IsNullOrWhiteSpace(Endpoint) ? DefaultEndpoint : Endpoint.Trim();
        return endpoint.TrimEnd('/');
    }

    /// <summary>
    /// The gateway key, if any: <c>OMNIROUTE_KEY</c> wins over the configured value.
    ///
    /// v1.0.16 removed the built-in fallback key. Nothing in the build ships a credential any more;
    /// when this returns null the UI sends the user to the setup screen instead of silently trying
    /// to call a provider with somebody else's key.
    /// </summary>
    public string? ResolveApiKey()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("OMNIROUTE_KEY");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment.Trim();
        }

        return string.IsNullOrWhiteSpace(ApiKey) ? null : ApiKey.Trim();
    }
}
