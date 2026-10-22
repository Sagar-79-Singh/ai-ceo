using System.Text.Json.Serialization;

namespace AiCeo.Models;

/// <summary>A single CEO delegation step. Used for strict parsing of the planner output.</summary>
public sealed class PlanStep
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("instructions")]
    public string Instructions { get; set; } = string.Empty;
}

/// <summary>A worker result handed back to the CEO for synthesis.</summary>
public sealed record WorkerResult(string AgentName, string Role, string Instructions, string Output);
