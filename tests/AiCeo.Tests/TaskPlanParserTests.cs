using AiCeo.Services;
using Xunit;

namespace AiCeo.Tests;

public class TaskPlanParserTests
{
    internal static List<AiCeo.Models.PlanStep> ParseOrEmpty(string? raw)
        => TaskPlanParser.TryParse(raw, out var steps) ? steps : new List<AiCeo.Models.PlanStep>();

    [Fact]
    public void Parses_a_plain_json_array()
    {
        Assert.True(TaskPlanParser.TryParse(
            """[{"role":"Researcher","instructions":"Find facts"}]""",
            out var steps));

        Assert.Single(steps);
        Assert.Equal("Researcher", steps[0].Role);
        Assert.Equal("Find facts", steps[0].Instructions);
    }

    [Fact]
    public void Parses_json_wrapped_in_prose_and_fences()
    {
        const string raw = """
            Here is the plan you asked for:

            ```json
            [{"role":"Analyst","instructions":"Model the numbers"}]
            ```

            Let me know!
            """;

        Assert.True(TaskPlanParser.TryParse(raw, out var steps));
        Assert.Equal("Analyst", Assert.Single(steps).Role);
    }

    [Fact]
    public void Drops_steps_without_a_role_or_instructions()
    {
        Assert.True(TaskPlanParser.TryParse(
            """[{"role":"","instructions":"orphan"},{"role":"Writer","instructions":"Draft it"}]""",
            out var steps));

        Assert.Equal("Writer", Assert.Single(steps).Role);
    }

    [Fact]
    public void Rejects_garbage_instead_of_throwing()
    {
        Assert.False(TaskPlanParser.TryParse("not json at all", out var steps));
        Assert.Empty(steps);

        Assert.False(TaskPlanParser.TryParse("""{"role":"Researcher"}""", out _));
        Assert.False(TaskPlanParser.TryParse("", out _));
        Assert.False(TaskPlanParser.TryParse(null, out _));
        Assert.False(TaskPlanParser.TryParse("[{\"role\":\"X\",\"instructions\":\"", out _));
    }

    [Fact]
    public void Caps_the_plan_length()
    {
        var steps = string.Join(",", Enumerable.Range(0, 25)
            .Select(index => $$"""{"role":"Worker{{index}}","instructions":"Task {{index}}"}"""));

        Assert.True(TaskPlanParser.TryParse($"[{steps}]", out var parsed));
        Assert.Equal(TaskPlanParser.MaxSteps, parsed.Count);
    }
}
