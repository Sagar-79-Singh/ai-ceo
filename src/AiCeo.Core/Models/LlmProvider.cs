namespace AiCeo.Models;

/// <summary>
/// The LLM backends an agent can be pointed at. Every provider exposes an OpenAI-compatible
/// surface, so a single <c>OpenAI</c> .NET client (with a different endpoint + key) drives all
/// of them.
/// </summary>
public enum LlmProvider
{
    /// <summary>Local OmniRoute gateway (default).</summary>
    OmniRoute = 0,

    /// <summary>api.openai.com.</summary>
    OpenAI = 1,

    /// <summary>Gemini's OpenAI-compatible endpoint.</summary>
    Gemini = 2,

    /// <summary>Any other OpenAI-compatible server; the user supplies the base URL.</summary>
    Custom = 3
}

/// <summary>
/// Static facts about one provider: where it lives, which environment variable can supply the key,
/// and what to tell the user.
///
/// v1.0.16 removed <c>DefaultModel</c>: a model is now always something the user chooses in the
/// first-run setup (or on a single agent), never a value baked into the build.
/// </summary>
public sealed record LlmProviderInfo(
    LlmProvider Provider,
    string DisplayName,
    string DefaultBaseUrl,
    string ApiKeyEnvironmentVariable,
    string Hint,
    bool RequiresApiKey)
{
    /// <summary>Value used by the &lt;select&gt; in the agent modal.</summary>
    public string Value => Provider.ToString();
}

/// <summary>Single source of truth for provider defaults, key environment variables and hints.</summary>
public static class LlmProviderCatalog
{
    public const string OmniRouteBaseUrl = "http://localhost:20128/v1";
    public const string OpenAIBaseUrl = "https://api.openai.com/v1";
    public const string GeminiBaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai";

    public static IReadOnlyList<LlmProviderInfo> All { get; } = new[]
    {
        new LlmProviderInfo(
            LlmProvider.OmniRoute,
            "OmniRoute",
            OmniRouteBaseUrl,
            "OMNIROUTE_KEY",
            "Local gateway on port 20128. Enter the model id your gateway exposes.",
            RequiresApiKey: true),
        new LlmProviderInfo(
            LlmProvider.OpenAI,
            "OpenAI",
            OpenAIBaseUrl,
            "OPENAI_API_KEY",
            "Direct to api.openai.com. Enter the model id from your OpenAI dashboard.",
            RequiresApiKey: true),
        new LlmProviderInfo(
            LlmProvider.Gemini,
            "Gemini",
            GeminiBaseUrl,
            "GEMINI_API_KEY",
            "Google's OpenAI-compatible endpoint. Enter the Gemini model id.",
            RequiresApiKey: true),
        new LlmProviderInfo(
            LlmProvider.Custom,
            "Custom",
            string.Empty,
            "CUSTOM_API_KEY",
            "Any OpenAI-compatible server (Ollama, vLLM, Azure proxy…). Base URL is required.",
            RequiresApiKey: true)
    };

    public static LlmProviderInfo Get(LlmProvider provider)
        => All.First(info => info.Provider == provider);

    public static string DefaultBaseUrlFor(LlmProvider provider) => Get(provider).DefaultBaseUrl;

    public static string DisplayNameFor(LlmProvider provider) => Get(provider).DisplayName;

    /// <summary>Parses a provider name coming from JSON, a query string, or a form post.</summary>
    public static bool TryParse(string? value, out LlmProvider provider)
    {
        if (Enum.TryParse(value, ignoreCase: true, out provider) && Enum.IsDefined(provider))
        {
            return true;
        }

        // JSON may carry the numeric value instead of the name.
        if (int.TryParse(value, out var number) && Enum.IsDefined(typeof(LlmProvider), number))
        {
            provider = (LlmProvider)number;
            return true;
        }

        provider = LlmProvider.OmniRoute;
        return false;
    }
}
