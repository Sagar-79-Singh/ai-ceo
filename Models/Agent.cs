using System.ComponentModel.DataAnnotations;

namespace AiCeo.Models;

/// <summary>
/// An agent in the star topology. Exactly one agent may have <see cref="IsManager"/> set;
/// that agent is the CEO and is the only agent the user ever talks to.
/// </summary>
public class Agent
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required, MaxLength(120)]
    public string Name { get; set; } = string.Empty;

    [Required, MaxLength(160)]
    public string Role { get; set; } = string.Empty;

    [Required]
    public string SystemPrompt { get; set; } = string.Empty;

    /// <summary>Model id understood by the selected provider, e.g. <c>gh/gpt-5.4-nano</c>.</summary>
    [Required, MaxLength(200)]
    public string ModelId { get; set; } = LlmProviderCatalog.OmniRouteDefaultModel;

    /// <summary>Which OpenAI-compatible backend this agent talks to.</summary>
    [MaxLength(32)]
    public LlmProvider Provider { get; set; } = LlmProvider.OmniRoute;

    /// <summary>
    /// Optional override of the provider's base URL. Empty means "use the provider default"
    /// (for <see cref="LlmProvider.Custom"/> it is required).
    /// </summary>
    [MaxLength(500)]
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Optional per-agent API key, stored in the local SQLite file. When empty the provider's
    /// environment variable is used instead. Never returned to the browser in clear text.
    /// </summary>
    [MaxLength(500)]
    public string? ApiKey { get; set; }

    public bool IsManager { get; set; }
}
