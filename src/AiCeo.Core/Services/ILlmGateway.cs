using AiCeo.Models;

namespace AiCeo.Services;

/// <summary>A function the model may call, advertised with a JSON Schema for its arguments.</summary>
public sealed record LlmToolDefinition(string Name, string Description, string ParametersJson);

/// <summary>One message in a tool-enabled conversation.</summary>
public abstract record LlmMessage;

/// <summary>Role: system.</summary>
public sealed record LlmSystemMessage(string Text) : LlmMessage;

/// <summary>Role: user.</summary>
public sealed record LlmUserMessage(string Text) : LlmMessage;

/// <summary>Role: assistant, optionally carrying the tool calls the model wants executed.</summary>
public sealed record LlmAssistantMessage(string? Text, IReadOnlyList<LlmToolCallRequest> ToolCalls) : LlmMessage
{
    public static LlmAssistantMessage FromText(string text) => new(text, Array.Empty<LlmToolCallRequest>());
}

/// <summary>Role: tool — the result of one call, matched back by <see cref="ToolCallId"/>.</summary>
public sealed record LlmToolMessage(string ToolCallId, string Content) : LlmMessage;

/// <summary>A tool call requested by the model.</summary>
public sealed record LlmToolCallRequest(string Id, string Name, string ArgumentsJson);

/// <summary>What the model produced in one round trip.</summary>
public sealed record LlmReply(string? Text, IReadOnlyList<LlmToolCallRequest> ToolCalls, string FinishReason)
{
    public bool WantsTools => ToolCalls.Count > 0;
}

/// <summary>Everything needed for one conversation turn against one provider connection.</summary>
public sealed record LlmCall(
    string AgentName,
    LlmProvider Provider,
    string BaseUrl,
    string ApiKey,
    string ModelId,
    IReadOnlyList<LlmMessage> Messages,
    IReadOnlyList<LlmToolDefinition> Tools,
    string? ToolChoice = null)
{
    public static LlmCall Probe(string agentName, LlmProvider provider, string baseUrl, string apiKey, string modelId)
        => new(agentName, provider, baseUrl, apiKey, modelId, Array.Empty<LlmMessage>(), Array.Empty<LlmToolDefinition>());
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
/// <c>OpenAI</c> .NET client (function calling included); faked in tests so sequencing, topology
/// and the tool loop can be asserted without a network.
/// </summary>
public interface ILlmGateway
{
    /// <summary>
    /// One round trip. Returns text and/or tool calls. Throws <see cref="LlmException"/> on
    /// provider problems.
    /// </summary>
    Task<LlmReply> CompleteAsync(LlmCall call, CancellationToken cancellationToken = default);

    /// <summary>Non-throwing reachability probe used by the sidebar badge.</summary>
    Task<ProviderHealth> CheckAsync(LlmCall probe, CancellationToken cancellationToken = default);
}
