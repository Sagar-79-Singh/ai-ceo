using System.ComponentModel.DataAnnotations;
using AiCeo.Options;

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

    /// <summary>OmniRoute model id, e.g. <c>gh/gpt-5.4-nano</c>.</summary>
    [Required, MaxLength(200)]
    public string ModelId { get; set; } = OmniRouteOptions.DefaultModel;

    public bool IsManager { get; set; }
}
