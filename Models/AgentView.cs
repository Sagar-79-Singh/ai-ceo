namespace AiCeo.Models;

/// <summary>
/// Read model for the sidebar, the agent modal and the REST API. It carries everything the UI
/// needs but never the raw API key — only a mask plus where the effective key comes from.
/// </summary>
public sealed record AgentView(
    Guid Id,
    string Name,
    string Role,
    string SystemPrompt,
    string ModelId,
    LlmProvider Provider,
    string ProviderDisplayName,
    string? BaseUrl,
    string EffectiveBaseUrl,
    string? ApiKeyMask,
    string ApiKeySource,
    bool HasStoredApiKey,
    bool IsManager)
{
    public static AgentView From(
        Agent agent,
        string effectiveBaseUrl,
        string? apiKeyMask,
        string apiKeySource)
        => new(
            agent.Id,
            agent.Name,
            agent.Role,
            agent.SystemPrompt,
            agent.ModelId,
            agent.Provider,
            LlmProviderCatalog.DisplayNameFor(agent.Provider),
            agent.BaseUrl,
            effectiveBaseUrl,
            apiKeyMask,
            apiKeySource,
            !string.IsNullOrWhiteSpace(agent.ApiKey),
            agent.IsManager);
}
