using AiCeo.Models;

namespace AiCeo.Services;

/// <summary>
/// A provider/connection problem that the orchestrator treats as a soft failure: the run keeps
/// going, the worker is marked as failed in the execution feed, and the CEO synthesizes what it
/// has.
/// </summary>
public sealed class LlmException : Exception
{
    public LlmException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }

    public LlmException(LlmProvider provider, string endpoint, string modelId, string message, Exception? innerException = null)
        : base($"{LlmProviderCatalog.DisplayNameFor(provider)} ({endpoint}, model '{modelId}'): {message}", innerException)
    {
        Provider = provider;
        Endpoint = endpoint;
        ModelId = modelId;
    }

    public LlmProvider? Provider { get; }

    public string? Endpoint { get; }

    public string? ModelId { get; }
}
