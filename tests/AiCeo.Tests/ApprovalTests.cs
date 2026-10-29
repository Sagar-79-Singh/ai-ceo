using AiCeo.Models;
using AiCeo.Options;
using AiCeo.Services;
using AiCeo.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using MsOptions = Microsoft.Extensions.Options.Options;
using Xunit;

namespace AiCeo.Tests;

/// <summary>
/// v2.0.0: the human-in-the-loop gate. These tests pin the two halves of the feature — the policy
/// (which tools ask) and the wiring (a rejected call must not touch the disk, and its reason must
/// reach the model).
/// </summary>
public class ApprovalTests
{
    [Theory]
    [InlineData(ToolCatalog.WriteFile, true)]
    [InlineData(ToolCatalog.RunCommand, true)]
    [InlineData(ToolCatalog.ReadFile, false)]
    [InlineData(ToolCatalog.ListFiles, false)]
    [InlineData("something-else", false)]
    public void Only_writes_and_commands_ask(string toolName, bool expected)
        => Assert.Equal(expected, ApprovalPolicy.Requires(toolName));

    [Fact]
    public async Task A_request_with_nothing_attached_is_approved_immediately()
    {
        // Headless runs (tests, --self-test) must behave exactly as they did before v2.0.0.
        var coordinator = new ApprovalCoordinator();

        var decision = await coordinator.RequestAsync(new PendingApproval { ToolName = ToolCatalog.WriteFile });

        Assert.True(decision.Approved);
        Assert.Null(decision.Reason);
        Assert.Null(coordinator.Current);
    }

    [Fact]
    public async Task An_attached_ui_parks_the_call_until_it_answers()
    {
        var coordinator = new ApprovalCoordinator();
        var published = new List<PendingApproval?>();

        coordinator.Attach(request => published.Add(request));

        var parked = coordinator.RequestAsync(new PendingApproval
        {
            ToolName = ToolCatalog.RunCommand,
            Target = "echo hi",
            Summary = "Run a terminal command"
        });

        Assert.False(parked.IsCompleted);
        Assert.NotNull(coordinator.Current);
        Assert.Contains(published, request => request is not null && request.ToolName == ToolCatalog.RunCommand);

        Assert.True(coordinator.Resolve(coordinator.Current!.Id, approved: false, reason: "not today"));

        var decision = await parked;

        Assert.False(decision.Approved);
        Assert.Equal("not today", decision.Reason);
        Assert.Null(coordinator.Current);
        Assert.Equal(0, coordinator.WaitingCount);
    }

    [Fact]
    public async Task A_rejection_without_a_reason_still_carries_one()
    {
        var coordinator = new ApprovalCoordinator();
        coordinator.Attach(_ => { });

        var parked = coordinator.RequestAsync(new PendingApproval { ToolName = ToolCatalog.WriteFile });

        coordinator.Resolve(coordinator.Current!.Id, approved: false, reason: "   ");

        var decision = await parked;

        Assert.False(decision.Approved);
        Assert.Equal(ApprovalDecision.DefaultRejectionReason, decision.Reason);
    }

    [Fact]
    public async Task Disarming_the_gate_lets_every_call_through()
    {
        var coordinator = new ApprovalCoordinator { Enabled = false };
        coordinator.Attach(_ => { });

        var decision = await coordinator.RequestAsync(new PendingApproval { ToolName = ToolCatalog.RunCommand });

        Assert.True(decision.Approved);
    }

    [Fact]
    public async Task Resolve_all_unparks_everything_when_the_run_is_stopped()
    {
        var coordinator = new ApprovalCoordinator();
        coordinator.Attach(_ => { });

        var first = coordinator.RequestAsync(new PendingApproval { ToolName = ToolCatalog.WriteFile });
        var second = coordinator.RequestAsync(new PendingApproval { ToolName = ToolCatalog.RunCommand });

        Assert.Equal(2, coordinator.ResolveAll(false, "the run was stopped by the user."));

        Assert.False((await first).Approved);
        Assert.False((await second).Approved);
        Assert.Equal(0, coordinator.WaitingCount);
    }

    [Fact]
    public void A_new_file_is_previewed_line_by_line()
    {
        var preview = ToolPreview.ForWrite("greet.py", existing: null, content: "print('hi')\nprint('bye')");

        Assert.Contains("new file", preview, StringComparison.Ordinal);
        Assert.Contains("+ print('hi')", preview, StringComparison.Ordinal);
        Assert.Contains("+ print('bye')", preview, StringComparison.Ordinal);
    }

    [Fact]
    public void An_overwrite_is_previewed_as_a_diff_against_disk()
    {
        var preview = ToolPreview.ForWrite(
            "greet.py",
            existing: "print('old')\nkeep me\n",
            content: "print('new')\nkeep me\n");

        Assert.Contains("- print('old')", preview, StringComparison.Ordinal);
        Assert.Contains("+ print('new')", preview, StringComparison.Ordinal);
        Assert.DoesNotContain("- keep me", preview, StringComparison.Ordinal);
    }

    [Fact]
    public void A_command_is_previewed_verbatim()
    {
        Assert.Equal("$ dotnet build", ToolPreview.ForCommand(" dotnet build "));
    }

    [Fact]
    public async Task A_rejected_write_is_not_executed_and_the_reason_reaches_the_model()
    {
        using var database = TestDatabase.Create();
        using var workspace = new TempWorkspace();

        database.SeedAgents(TestAgents.Ceo(), TestAgents.Worker("Forge", "Software Engineer", tools: true));

        var coordinator = new ApprovalCoordinator();

        // The UI answers instantly: refuse every write.
        coordinator.Attach(request =>
        {
            if (request is not null)
            {
                coordinator.Resolve(request.Id, approved: false, reason: "keep my repository clean");
            }
        });

        var gateway = WriteOnceGateway();

        var task = await CreateOrchestrator(database, gateway, workspace, coordinator)
            .RunAsync("Write a python hello world");

        Assert.False(workspace.Exists("greet.py"));

        // The refusal is on the record, and the model was told about it.
        Assert.Contains(task.Logs, log => log.Action == LogActions.Approval);
        Assert.Contains(task.Logs, log => log.Action == LogActions.ApprovalResult
            && log.Content.Contains("rejected", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(task.Logs, log => log.Action == LogActions.ToolResult);

        var retry = gateway.Calls.Last(call => call.AgentName == "Forge");
        var rejection = retry.Messages.OfType<LlmToolMessage>().Single();

        Assert.Contains("REJECTED", rejection.Content, StringComparison.Ordinal);
        Assert.Contains("keep my repository clean", rejection.Content, StringComparison.Ordinal);

        // The CEO still gets an answer, and it is told what was refused.
        Assert.Contains("CEO final", task.FinalOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_approved_write_runs_exactly_as_before()
    {
        using var database = TestDatabase.Create();
        using var workspace = new TempWorkspace();

        database.SeedAgents(TestAgents.Ceo(), TestAgents.Worker("Forge", "Software Engineer", tools: true));

        var coordinator = new ApprovalCoordinator();
        coordinator.Attach(request =>
        {
            if (request is not null)
            {
                coordinator.Resolve(request.Id, approved: true, reason: null);
            }
        });

        var task = await CreateOrchestrator(database, gateway: WriteOnceGateway(), workspace, coordinator)
            .RunAsync("Write a python hello world");

        Assert.True(workspace.Exists("greet.py"));
        Assert.Equal("print('hi')", workspace.Read("greet.py"));
        Assert.Contains(task.Logs, log => log.Action == LogActions.ApprovalResult
            && log.Content.StartsWith("approved", StringComparison.OrdinalIgnoreCase));
    }

    private static FakeLlmGateway WriteOnceGateway() => new()
    {
        Responder = call =>
        {
            if (call.Messages.OfType<LlmSystemMessage>().Any(system =>
                    system.Text.Contains(AgentOrchestrator.PlanningContractMarker, StringComparison.Ordinal)))
            {
                return FakeLlmGateway.Text("""[{"role":"Software Engineer","instructions":"Create greet.py"}]""");
            }

            if (call.Messages.OfType<LlmSystemMessage>().Any(system =>
                    system.Text.Contains(AgentOrchestrator.SynthesisContractMarker, StringComparison.Ordinal)))
            {
                return FakeLlmGateway.Text("CEO final: the request was handled.");
            }

            // The tool worker: ask to write once, then summarise whatever came back.
            return call.Messages.OfType<LlmToolMessage>().Any()
                ? FakeLlmGateway.Text("Summary for the CEO.")
                : new LlmReply(
                    null,
                    new[] { new LlmToolCallRequest("call_1", "write_file", """{"filePath":"greet.py","content":"print('hi')"}""") },
                    "ToolCalls");
        }
    };

    private static AgentOrchestrator CreateOrchestrator(
        TestDatabase database,
        FakeLlmGateway gateway,
        TempWorkspace workspace,
        ApprovalCoordinator coordinator)
    {
        database.SeedWorkspace(workspace.Root);
        database.SeedConnection();

        var workspaceService = new WorkspaceService(database.Factory);
        var tools = new ToolService(workspaceService, NullLogger<ToolService>.Instance);

        return new AgentOrchestrator(
            database.Factory,
            gateway,
            tools,
            workspaceService,
            database.Setup(),
            MsOptions.Create(new OmniRouteOptions()),
            NullLogger<AgentOrchestrator>.Instance,
            coordinator);
    }
}
