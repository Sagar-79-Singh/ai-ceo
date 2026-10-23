using System.Text.Json;
using AiCeo.Models;

namespace AiCeo.Services;

/// <summary>Serialization helpers shared by the orchestrator and the UI.</summary>
public static class PlanJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public static string Serialize(IReadOnlyList<PlanStep> steps) => JsonSerializer.Serialize(steps, Options);

    public static List<PlanStep> Parse(string? json)
        => TaskPlanParser.TryParse(json, out var steps) ? steps : new List<PlanStep>();
}
