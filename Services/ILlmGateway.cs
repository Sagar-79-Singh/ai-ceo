using AiCeo.Models;

namespace AiCeo.Services;

/// <summary>One chat turn against one provider connection.</summary>
public sealed record LlmCall(
    string AgentName,
    LlmProvider Provider,
    string BaseUrl,
    string ApiKey,
    string ModelId,
    string SystemPrompt,
    string UserPrompt)
{
    public static LlmCall Probe(string agentName, LlmProvider provider, string baseUrl, string apiKey, string modelId)
        => new(agentName, provider, baseUrl, apiKey, modelId, string.Empty, string.Empty);
}

/// <summary>Result of a lightweight provider reachability probe.</summary>
public sealed record ProviderHealth(
    bool Online,
    string Message,
    string ProviderDisplayName,
    string Endpoint,
    string ModelId)
{
    public static ProviderHealth For(LlmConnection connection, bool online, string message)
        => new(online, message, connection.ProviderName, connection.BaseUrl, connection.ModelId);
}

/// <summary>
/// The single door to every LLM. Implemented by <see cref="LlmGateway"/> with the official
/// <c>OpenAI</c> .NET client; faked in tests so sequencing/topology can be asserted without a
/// network.
/// </summary>
public interface ILlmGateway
{
    /// <summary>Single-shot completion. Throws <see cref="LlmException"/> on provider problems.</summary>
    Task<string> CompleteAsync(LlmCall call, CancellationToken cancellationToken = default);

    /// <summary>Non-throwing reachability probe used by the sidebar badge.</summary>
    Task<ProviderHealth> CheckAsync(LlmCall probe, CancellationToken cancellationToken = default);
}
