namespace AiCeo.Models;

/// <summary>Payload produced by the create/edit agent modal.</summary>
public sealed class AgentFormModel
{
    public Guid? Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Role { get; set; } = string.Empty;

    public string SystemPrompt { get; set; } = string.Empty;

    public string ModelId { get; set; } = string.Empty;

    public LlmProvider Provider { get; set; } = LlmProvider.OmniRoute;

    /// <summary>Optional base URL override; blank means the provider default.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// A new API key. Blank on edit means "keep the stored key" (so the UI never has to send the
    /// secret back to the server just to save an unrelated change).
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>Explicitly drop the stored key (used together with a blank <see cref="ApiKey"/>).</summary>
    public bool ClearApiKey { get; set; }
}
