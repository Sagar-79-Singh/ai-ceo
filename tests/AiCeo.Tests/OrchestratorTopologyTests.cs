using AiCeo.Models;
using AiCeo.Options;
using AiCeo.Services;
using Microsoft.Extensions.Logging.Abstractions;
using MsOptions = Microsoft.Extensions.Options.Options;
using Xunit;

namespace AiCeo.Tests;

/// <summary>
/// Guards the two non-negotiables of the product: a strict star topology and strictly sequential
/// execution.
/// </summary>
public class OrchestratorTopologyTests
{
    private static AgentOrchestrator CreateOrchestrator(TestDatabase database, FakeLlmGateway gateway)
        => new(
            database.Db,
            gateway,
            MsOptions.Create(new OmniRouteOptions()),
            NullLogger<AgentOrchestrator>.Instance);

    [Fact]
    public async Task Runs_are_sequential_and_ordered_plan_workers_synthesis()
    {
        using var database = TestDatabase.Create();
        database.SeedAgents(
            TestAgents.Ceo(),
            TestAgents.Worker("Scout", "Researcher"),
            TestAgents.Worker("Vector", "Analyst"));

        var gateway = new FakeLlmGateway
        {
            Responder = call =>
            {
                if (call.SystemPrompt.Contains(AgentOrchestrator.PlanningContractMarker, StringComparison.Ordinal))
                {
                    return """
                        [{"role":"Researcher","instructions":"Research the market"},
                         {"role":"Analyst","instructions":"Size the opportunity"}]
                        """;
                }

                if (call.SystemPrompt.Contains(AgentOrchestrator.SynthesisContractMarker, StringComparison.Ordinal))
                {
                    return "FINAL ANSWER";
                }

                return $"{call.AgentName} says hi";
            }
        };

        var task = await CreateOrchestrator(database, gateway).RunAsync("Launch a product");

        // 1 plan + 2 workers + 1 synthesis, in that exact order.
        Assert.Equal(4, gateway.Calls.Count);
        Assert.Contains(AgentOrchestrator.PlanningContractMarker, gateway.Calls[0].SystemPrompt, StringComparison.Ordinal);
        Assert.Equal("Scout", gateway.Calls[1].AgentName);
        Assert.Equal("Vector", gateway.Calls[2].AgentName);
        Assert.Contains(AgentOrchestrator.SynthesisContractMarker, gateway.Calls[3].SystemPrompt, StringComparison.Ordinal);

        // Strictly sequential: never more than one in-flight LLM call.
        Assert.Equal(1, gateway.MaxConcurrentCalls);

        Assert.Equal("FINAL ANSWER", task.FinalOutput);

        // The feed is persisted: plan, two delegations, two results.
        var actions = task.Logs.OrderBy(log => log.CreatedAt).Select(log => log.Action).ToList();
        Assert.Equal(2, actions.Count(action => action == LogActions.Delegated));
        Assert.Equal(2, actions.Count(action => action == LogActions.Result));
        Assert.Single(actions, action => action == LogActions.Plan);
    }

    [Fact]
    public async Task Workers_never_see_each_other_and_never_get_the_user_prompt_directly()
    {
        using var database = TestDatabase.Create();
        database.SeedAgents(
            TestAgents.Ceo(),
            TestAgents.Worker("Scout", "Researcher"),
            TestAgents.Worker("Vector", "Analyst"));

        var gateway = new FakeLlmGateway
        {
            Responder = call =>
            {
                if (call.SystemPrompt.Contains(AgentOrchestrator.PlanningContractMarker, StringComparison.Ordinal))
                {
                    return """
                        [{"role":"Researcher","instructions":"Find facts"},
                         {"role":"Analyst","instructions":"Interpret facts"}]
                        """;
                }

                if (call.SystemPrompt.Contains(AgentOrchestrator.SynthesisContractMarker, StringComparison.Ordinal))
                {
                    return "done";
                }

                return $"{call.AgentName} output";
            }
        };

        await CreateOrchestrator(database, gateway).RunAsync("Do the thing");

        var scout = gateway.Calls.Single(call => call.AgentName == "Scout");
        var vector = gateway.Calls.Single(call => call.AgentName == "Vector");

        // A worker's system prompt carries only its own persona.
        Assert.Contains("You are Scout the Researcher.", scout.SystemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("You are Vector", scout.SystemPrompt, StringComparison.Ordinal);

        // Worker 1 gets the CEO's assignment, not the other worker's.
        Assert.Contains("Find facts", scout.UserPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Interpret facts", scout.UserPrompt, StringComparison.Ordinal);

        // The CEO is the only one who receives the worker outputs back.
        var synthesis = gateway.Calls[^1];
        Assert.Contains("Scout output", synthesis.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("Vector output", synthesis.UserPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failing_worker_is_logged_and_does_not_abort_the_run()
    {
        using var database = TestDatabase.Create();
        database.SeedAgents(
            TestAgents.Ceo(),
            TestAgents.Worker("Scout", "Researcher"),
            TestAgents.Worker("Vector", "Analyst"));

        var gateway = new FakeLlmGateway
        {
            Responder = call =>
            {
                if (call.SystemPrompt.Contains(AgentOrchestrator.PlanningContractMarker, StringComparison.Ordinal))
                {
                    return """
                        [{"role":"Researcher","instructions":"Find facts"},
                         {"role":"Analyst","instructions":"Interpret facts"}]
                        """;
                }

                if (call.SystemPrompt.Contains(AgentOrchestrator.SynthesisContractMarker, StringComparison.Ordinal))
                {
                    return "recovered";
                }

                if (call.AgentName == "Scout")
                {
                    throw new LlmException(LlmProvider.OpenAI, "https://api.openai.com/v1", "gpt-4o-mini", "boom");
                }

                return "Vector output";
            }
        };

        var task = await CreateOrchestrator(database, gateway).RunAsync("Do the thing");

        Assert.Equal("recovered", task.FinalOutput);
        Assert.Contains(task.Logs, log => log.Action == LogActions.Error && log.AgentName == "Scout");
        Assert.Contains(task.Logs, log => log.Action == LogActions.Result && log.AgentName == "Vector");
        Assert.Equal("Vector output", task.Logs.Single(log => log.Action == LogActions.Result).Content);
    }

    [Fact]
    public async Task An_unparseable_plan_falls_back_to_a_single_delegation()
    {
        using var database = TestDatabase.Create();
        database.SeedAgents(TestAgents.Ceo(), TestAgents.Worker("Scout", "Researcher"));

        var gateway = new FakeLlmGateway
        {
            Responder = call =>
            {
                if (call.SystemPrompt.Contains(AgentOrchestrator.PlanningContractMarker, StringComparison.Ordinal))
                {
                    return "Sure! Here is my plan: do the thing. (no JSON at all)";
                }

                if (call.SystemPrompt.Contains(AgentOrchestrator.SynthesisContractMarker, StringComparison.Ordinal))
                {
                    return "fallback answer";
                }

                return "Scout output";
            }
        };

        var task = await CreateOrchestrator(database, gateway).RunAsync("Do the thing");

        Assert.Equal("fallback answer", task.FinalOutput);
        Assert.Contains(task.Logs, log => log.Action == LogActions.Error);
        Assert.Single(TaskPlanParserTests.ParseOrEmpty(task.CeoPlan));
        Assert.Equal("Scout", gateway.Calls[1].AgentName);
    }
}
