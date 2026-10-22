namespace AiCeo.Models;

/// <summary>Payload produced by the create/edit agent modal.</summary>
public sealed class AgentFormModel
{
    public Guid? Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Role { get; set; } = string.Empty;

    public string SystemPrompt { get; set; } = string.Empty;

    public string ModelId { get; set; } = string.Empty;
}
