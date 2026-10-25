using AiCeo.Models;
using AiCeo.Options;
using AiCeo.Services;
using AiCeo.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using MsOptions = Microsoft.Extensions.Options.Options;
using Xunit;

namespace AiCeo.Tests;

/// <summary>
/// Guards the product's non-negotiables: a strict star topology, strictly sequential execution,
/// and a bounded agentic tool loop that only tool-enabled workers can enter.
/// </summary>
public class OrchestratorTopologyTests
{
    /// <summary>
    /// v1.0.16: a run needs a saved provider connection. Nothing is compiled into the build any
    /// more, so the tests seed the same row the first-run setup writes; <paramref name="configure"/>
    /// lets one test start from an empty install.
    /// </summary>
    private static AgentOrchestrator CreateOrchestrator(
        TestDatabase database,
        FakeLlmGateway gateway,
        TempWorkspace? workspace = null,
        bool configure = true)
    {
        if (workspace is not null)
        {
            database.SeedWorkspace(workspace.Root);
        }

        if (configure)
        {
            database.SeedConnection();
        }

        var workspaceService = new WorkspaceService(database.Factory);
        var tools = new ToolService(workspaceService, NullLogger<ToolService>.Instance);

        return new AgentOrchestrator(
            database.Factory,
            gateway,
            tools,
            workspaceService,
            database.Setup(),
            MsOptions.Create(new OmniRouteOptions()),
            NullLogger<AgentOrchestrator>.Instance);
    }

    private static bool IsPlanning(LlmCall call) => call.Messages.OfType<LlmSystemMessage>()
        .Any(system => system.Text.Contains(AgentOrchestrator.PlanningContractMarker, StringComparison.Ordinal));

    private static bool IsSynthesis(LlmCall call) => call.Messages.OfType<LlmSystemMessage>()
        .Any(system => system.Text.Contains(AgentOrchestrator.SynthesisContractMarker, StringComparison.Ordinal));

    private static string UserText(LlmCall call) => string.Join(
        "\n",
        call.Messages.OfType<LlmUserMessage>().Select(message => message.Text));

    private static string SystemText(LlmCall call) => string.Join(
        "\n",
        call.Messages.OfType<LlmSystemMessage>().Select(message => message.Text));

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
                if (IsPlanning(call))
                {
                    return FakeLlmGateway.Text("""
                        [{"role":"Researcher","instructions":"Research the market"},
                         {"role":"Analyst","instructions":"Size the opportunity"}]
                        """);
                }

                if (IsSynthesis(call))
                {
                    return FakeLlmGateway.Text("FINAL ANSWER");
                }

                return FakeLlmGateway.Text($"{call.AgentName} says hi");
            }
        };

        var task = await CreateOrchestrator(database, gateway).RunAsync("Launch a product");

        // 1 plan + 2 workers + 1 synthesis, in that exact order.
        Assert.Equal(4, gateway.Calls.Count);
        Assert.True(IsPlanning(gateway.Calls[0]));
        Assert.Equal("Scout", gateway.Calls[1].AgentName);
        Assert.Equal("Vector", gateway.Calls[2].AgentName);
        Assert.True(IsSynthesis(gateway.Calls[3]));

        // Strictly sequential: never more than one in-flight LLM call.
        Assert.Equal(1, gateway.MaxConcurrentCalls);

        Assert.Equal("FINAL ANSWER", task.FinalOutput);

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
                if (IsPlanning(call))
                {
                    return FakeLlmGateway.Text("""
                        [{"role":"Researcher","instructions":"Find facts"},
                         {"role":"Analyst","instructions":"Interpret facts"}]
                        """);
                }

                if (IsSynthesis(call))
                {
                    return FakeLlmGateway.Text("done");
                }

                return FakeLlmGateway.Text($"{call.AgentName} output");
            }
        };

        await CreateOrchestrator(database, gateway).RunAsync("Do the thing");

        var scout = gateway.Calls.Single(call => call.AgentName == "Scout");
        var vector = gateway.Calls.Single(call => call.AgentName == "Vector");

        // A worker's system prompt carries only its own persona.
        Assert.Contains("You are Scout the Researcher.", SystemText(scout), StringComparison.Ordinal);
        Assert.DoesNotContain("You are Vector", SystemText(scout), StringComparison.Ordinal);

        // Worker 1 gets the CEO's assignment, not the other worker's.
        Assert.Contains("Find facts", UserText(scout), StringComparison.Ordinal);
        Assert.DoesNotContain("Interpret facts", UserText(scout), StringComparison.Ordinal);

        // The CEO is the only one who receives the worker outputs back.
        var synthesis = gateway.Calls[^1];
        Assert.Contains("Scout output", UserText(synthesis), StringComparison.Ordinal);
        Assert.Contains("Vector output", UserText(synthesis), StringComparison.Ordinal);
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
                if (IsPlanning(call))
                {
                    return FakeLlmGateway.Text("""
                        [{"role":"Researcher","instructions":"Find facts"},
                         {"role":"Analyst","instructions":"Interpret facts"}]
                        """);
                }

                if (IsSynthesis(call))
                {
                    return FakeLlmGateway.Text("recovered");
                }

                if (call.AgentName == "Scout")
                {
                    throw new LlmException(LlmProvider.OpenAI, "https://api.openai.com/v1", "gpt-4o-mini", "boom");
                }

                return FakeLlmGateway.Text("Vector output");
            }
        };

        var task = await CreateOrchestrator(database, gateway).RunAsync("Do the thing");

        Assert.Equal("recovered", task.FinalOutput);
        Assert.Contains(task.Logs, log => log.Action == LogActions.Error && log.AgentName == "Scout");
        Assert.Contains(task.Logs, log => log.Action == LogActions.Result && log.AgentName == "Vector");
        Assert.Equal("Vector output", task.Logs.Single(log => log.Action == LogActions.Result).Content);
        Assert.Equal(1, gateway.MaxConcurrentCalls);
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
                if (IsPlanning(call))
                {
                    return FakeLlmGateway.Text("Sure! Here is my plan: do the thing. (no JSON at all)");
                }

                if (IsSynthesis(call))
                {
                    return FakeLlmGateway.Text("fallback answer");
                }

                return FakeLlmGateway.Text("Scout output");
            }
        };

        var task = await CreateOrchestrator(database, gateway).RunAsync("Do the thing");

        Assert.Equal("fallback answer", task.FinalOutput);
        Assert.Contains(task.Logs, log => log.Action == LogActions.Error);
        Assert.Single(TaskPlanParserTests.ParseOrEmpty(task.CeoPlan));
        Assert.Equal("Scout", gateway.Calls[1].AgentName);
    }

    // ---------------------------------------------------------------- agentic tool loop

    [Fact]
    public async Task A_tool_worker_writes_files_then_reports_back_to_the_ceo()
    {
        using var database = TestDatabase.Create();
        using var workspace = new TempWorkspace();

        database.SeedAgents(
            TestAgents.Ceo(),
            TestAgents.Worker("Forge", "Software Engineer", tools: true));

        const string fileJson = """{"filePath":"greet.py","content":"print('hi')"}""";

        var gateway = new FakeLlmGateway
        {
            Responder = call =>
            {
                if (IsPlanning(call))
                {
                    return FakeLlmGateway.Text("""[{"role":"Software Engineer","instructions":"Create greet.py"}]""");
                }

                if (IsSynthesis(call))
                {
                    return FakeLlmGateway.Text("CEO final: greet.py was created.");
                }

                // The tool-enabled worker: first call writes the file, second call summarises.
                var hasToolResult = call.Messages.OfType<LlmToolMessage>().Any();
                return hasToolResult
                    ? FakeLlmGateway.Text("Created greet.py with a hello-world script.")
                    : new LlmReply(
                        null,
                        new[] { new LlmToolCallRequest("call_1", "write_file", fileJson) },
                        "ToolCalls");
            }
        };

        var task = await CreateOrchestrator(database, gateway, workspace).RunAsync("Write a python hello world");

        Assert.True(workspace.Exists("greet.py"));
        Assert.Equal("print('hi')", workspace.Read("greet.py"));

        // The loop ran: tool call advertised, executed, result fed back, then a text summary.
        var workerCalls = gateway.Calls.Where(call => call.AgentName == "Forge").ToList();
        Assert.Equal(2, workerCalls.Count);
        Assert.Contains(workerCalls, call => call.Tools.Count == ToolCatalog.Definitions.Count);
        Assert.Contains(workerCalls, call => call.Messages.OfType<LlmToolMessage>().Any());

        // The tool activity is visible in the feed and the CEO still wrote the final answer.
        Assert.Contains(task.Logs, log => log.Action == LogActions.Tool && log.ToolName == "write_file");
        Assert.Contains(task.Logs, log => log.Action == LogActions.ToolResult);
        Assert.Equal("CEO final: greet.py was created.", task.FinalOutput);
        Assert.Contains(task.Logs, log => log.Action == LogActions.Result && log.Content.Contains("greet.py"));
    }

    [Fact]
    public async Task A_tool_worker_can_run_commands_iteratively()
    {
        using var database = TestDatabase.Create();
        using var workspace = new TempWorkspace();

        database.SeedAgents(
            TestAgents.Ceo(),
            TestAgents.Worker("Forge", "Software Engineer", tools: true));

        var builderCommand = OperatingSystem.IsWindows() ? "echo built > build.txt" : "echo built > build.txt";

        var gateway = new FakeLlmGateway
        {
            Responder = call =>
            {
                if (IsPlanning(call))
                {
                    return FakeLlmGateway.Text("""[{"role":"Software Engineer","instructions":"Build it"}]""");
                }

                if (IsSynthesis(call))
                {
                    return FakeLlmGateway.Text("done");
                }

                var toolMessages = call.Messages.OfType<LlmToolMessage>().Count();
                return toolMessages switch
                {
                    0 => new LlmReply(null, new[] { new LlmToolCallRequest("c1", "write_file", """{"filePath":"a.txt","content":"a"}""") }, "ToolCalls"),
                    1 => new LlmReply(null, new[] { new LlmToolCallRequest("c2", "run_command", $$"""{"command":"{{builderCommand}}"}""") }, "ToolCalls"),
                    _ => FakeLlmGateway.Text("Wrote a.txt and ran the build.")
                };
            }
        };

        var task = await CreateOrchestrator(database, gateway, workspace).RunAsync("Build the project");

        Assert.True(workspace.Exists("a.txt"));
        Assert.True(workspace.Exists("build.txt"));

        var actions = task.Logs.Select(log => log.Action).ToList();
        Assert.Equal(2, actions.Count(action => action == LogActions.Tool));
        Assert.Equal(2, actions.Count(action => action == LogActions.ToolResult));
    }

    [Fact]
    public async Task A_worker_cannot_escape_the_workspace_and_keeps_going()
    {
        using var database = TestDatabase.Create();
        using var workspace = new TempWorkspace();

        database.SeedAgents(
            TestAgents.Ceo(),
            TestAgents.Worker("Forge", "Software Engineer", tools: true));

        var outsideAttempt = """{"filePath":"../escape.txt","content":"pwned"}""";

        var gateway = new FakeLlmGateway
        {
            Responder = call =>
            {
                if (IsPlanning(call))
                {
                    return FakeLlmGateway.Text("""[{"role":"Software Engineer","instructions":"Write outside"}]""");
                }

                if (IsSynthesis(call))
                {
                    return FakeLlmGateway.Text("denied");
                }

                var toolMessages = call.Messages.OfType<LlmToolMessage>().ToList();
                return toolMessages.Count == 0
                    ? new LlmReply(null, new[] { new LlmToolCallRequest("c1", "write_file", outsideAttempt) }, "ToolCalls")
                    : FakeLlmGateway.Text("The write was refused; nothing was created.");
            }
        };

        var task = await CreateOrchestrator(database, gateway, workspace).RunAsync("Escape the sandbox");

        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(workspace.Root)!, "escape.txt")));

        var denial = task.Logs.Single(log => log.Action == LogActions.ToolResult);
        Assert.Contains("outside the workspace", denial.Content, StringComparison.OrdinalIgnoreCase);

        // The refusal is fed back to the worker, which then answers.
        var workerCall = gateway.Calls.Last(call => call.AgentName == "Forge");
        var toolMessage = workerCall.Messages.OfType<LlmToolMessage>().Single();
        Assert.Contains("outside the workspace", toolMessage.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_worker_without_tools_never_receives_tool_definitions()
    {
        using var database = TestDatabase.Create();
        using var workspace = new TempWorkspace();

        database.SeedAgents(TestAgents.Ceo(), TestAgents.Worker("Scout", "Researcher", tools: false));

        var gateway = new FakeLlmGateway
        {
            Responder = call => IsPlanning(call)
                ? FakeLlmGateway.Text("""[{"role":"Researcher","instructions":"Find facts"}]""")
                : FakeLlmGateway.Text("no tools here")
        };

        await CreateOrchestrator(database, gateway, workspace).RunAsync("Research");

        Assert.All(gateway.Calls, call => Assert.Empty(call.Tools));
        Assert.DoesNotContain(TaskToolLogs(database), log => log.Action == LogActions.Tool);
    }

    [Fact]
    public async Task The_tool_loop_is_bounded()
    {
        using var database = TestDatabase.Create();
        using var workspace = new TempWorkspace();

        database.SeedAgents(TestAgents.Ceo(), TestAgents.Worker("Forge", "Software Engineer", tools: true));

        // A model that never stops calling tools must not loop forever.
        var gateway = new FakeLlmGateway
        {
            Responder = call =>
            {
                if (IsPlanning(call))
                {
                    return FakeLlmGateway.Text("""[{"role":"Software Engineer","instructions":"Loop"}]""");
                }

                if (IsSynthesis(call))
                {
                    return FakeLlmGateway.Text("stopped");
                }

                if (call.Tools.Count == 0)
                {
                    return FakeLlmGateway.Text("final wrap-up");
                }

                return new LlmReply(
                    null,
                    new[] { new LlmToolCallRequest(Guid.NewGuid().ToString("N"), "list_files", """{"directoryPath":"."}""") },
                    "ToolCalls");
            }
        };

        var task = await CreateOrchestrator(database, gateway, workspace).RunAsync("Loop forever");

        var toolCalls = task.Logs.Count(log => log.Action == LogActions.Tool);
        Assert.Equal(AgentOrchestrator.MaxToolRounds, toolCalls);
        Assert.Equal("stopped", task.FinalOutput);
    }

    [Fact]
    public async Task An_unconfigured_install_is_sent_to_the_setup_screen_before_any_token_is_spent()
    {
        using var database = TestDatabase.Create();
        database.SeedAgents(TestAgents.Ceo(), TestAgents.Worker("Scout", "Researcher"));

        var gateway = new FakeLlmGateway();
        var orchestrator = CreateOrchestrator(database, gateway, configure: false);

        var exception = await Assert.ThrowsAsync<LlmException>(() => orchestrator.RunAsync("Launch a product"));

        Assert.Contains("setup screen", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(gateway.Calls);
        Assert.Empty(await TaskToolLogs(database));
    }

    [Fact]
    public async Task The_saved_connection_supplies_the_model_for_every_agent()
    {
        using var database = TestDatabase.Create();
        database.SeedAgents(TestAgents.Ceo(), TestAgents.Worker("Scout", "Researcher"));

        var gateway = new FakeLlmGateway().ThenPlan("""[{"role":"Researcher","instructions":"Look"}]""");
        await CreateOrchestrator(database, gateway).RunAsync("Launch a product");

        Assert.NotEmpty(gateway.Calls);
        Assert.All(gateway.Calls, call => Assert.Equal("test-model", call.ModelId));
        Assert.All(gateway.Calls, call => Assert.Equal("sk-test-key-0123456789", call.ApiKey));
    }

    private static List<TaskLog> TaskToolLogs(TestDatabase database)
    {
        using var db = database.NewContext();
        return db.TaskLogs.ToList();
    }
}
