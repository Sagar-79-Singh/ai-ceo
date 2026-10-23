using System.Text.Json;
using AiCeo.Models;

namespace AiCeo.Services;

/// <summary>
/// Strict, defensive parsing of the CEO planner output (RULES.md rule 4). Models love to wrap
/// JSON in prose or markdown fences, so the payload is extracted and validated before use.
/// </summary>
public static class TaskPlanParser
{
    public const int MaxSteps = 8;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    public static bool TryParse(string? raw, out List<PlanStep> steps)
    {
        steps = new List<PlanStep>();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var text = StripCodeFences(raw.Trim());
        var start = text.IndexOf('[');
        var end = text.LastIndexOf(']');
        if (start < 0 || end <= start)
        {
            return false;
        }

        var json = text.Substring(start, end - start + 1);

        try
        {
            var parsed = JsonSerializer.Deserialize<List<PlanStep?>>(json, SerializerOptions);
            if (parsed is null)
            {
                return false;
            }

            steps = parsed
                .Where(step => step is not null)
                .Select(step => new PlanStep
                {
                    Role = (step!.Role ?? string.Empty).Trim(),
                    Instructions = (step.Instructions ?? string.Empty).Trim()
                })
                .Where(step => step.Role.Length > 0 && step.Instructions.Length > 0)
                .Take(MaxSteps)
                .ToList();

            return steps.Count > 0;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private static string StripCodeFences(string text)
    {
        if (!text.StartsWith("```", StringComparison.Ordinal))
        {
            return text;
        }

        var firstNewLine = text.IndexOf('\n');
        if (firstNewLine < 0)
        {
            return text;
        }

        var body = text[(firstNewLine + 1)..];
        var closingFence = body.LastIndexOf("```", StringComparison.Ordinal);
        return closingFence >= 0 ? body[..closingFence] : body;
    }
}
