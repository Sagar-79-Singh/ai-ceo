using AiCeo.Models;
using AiCeo.Options;
using AiCeo.Services;
using AiCeo.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MsOptions = Microsoft.Extensions.Options.Options;
using Xunit;

namespace AiCeo.Tests;

/// <summary>
/// v2.0.1 live agent chats: every delegation is recorded as its own CEO ↔ worker conversation, with
/// the instruction, the worker's reasoning, the commands it ran and the answer it handed back.
/// </summary>
public class WorkerThreadTests
{
    private static WorkerThreadStore CreateStore(TestDatabase database)
        => new(database.Factory, new WorkerThreadHub());

    [Fact]
    public async Task A_thread_keeps_the_instruction_every_step_and_the_answer()
    {
        using var database = TestDatabase.Create();
        var store = CreateStore(database);

        var taskId = Guid.NewGuid();
        var id = await store.BeginAsync(taskId, "Writer", "Writer", "Draft the release notes");

        await store.AppendAsync(id, ThreadEntryKind.Thought, "I will read the changelog first.");
        await store.AppendAsync(id, ThreadEntryKind.ToolCall, "{\"filePath\":\"CHANGELOG.md\"}", "read_file");
        await store.AppendAsync(id, ThreadEntryKind.ToolResult, "# Changelog", "read_file");
        await store.CompleteAsync(id, ThreadStatus.Completed, "Here are the notes.");

        var detail = await store.GetAsync(id);

        Assert.NotNull(detail);
        Assert.Equal("Writer", detail!.Thread.AgentName);
        Assert.Equal(ThreadStatus.Completed, detail.Thread.Status);
        Assert.Equal("Here are the notes.", detail.Output);

        Assert.Equal(
            new[] { "instruction", "thought", "tool_call", "tool_result", "output" },
            detail.Entries.Select(entry => entry.Kind).ToArray());

        Assert.Equal("read_file", detail.Entries[2].ToolName);
        Assert.Contains("Draft the release notes", detail.Entries[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Threads_are_listed_newest_first_and_per_agent()
    {
        using var database = TestDatabase.Create();
        var store = CreateStore(database);

        var task = Guid.NewGuid();
        var writer = await store.BeginAsync(task, "Writer", "Writer", "first");

        // Completed first, so the two threads have clearly different "last touched" times.
        await store.CompleteAsync(writer, ThreadStatus.Completed, "done");

        var forge = await store.BeginAsync(task, "Forge", "Engineer", "second");

        var all = await store.ListRecentAsync();
        Assert.Equal(2, all.Count);
        Assert.Equal(forge, all[0].Id);

        var writerThreads = await store.ListForAgentAsync("Writer");
        Assert.Single(writerThreads);

        var latest = await store.LatestForAgentAsync("Forge");
        Assert.NotNull(latest);
        Assert.Equal(forge, latest!.Id);

        Assert.Null(await store.LatestForAgentAsync("Nobody"));
    }

    [Fact]
    public async Task A_failed_delegation_is_recorded_as_failed()
    {
        using var database = TestDatabase.Create();
        var store = CreateStore(database);

        var id = await store.BeginAsync(Guid.NewGuid(), "Forge", "Engineer", "build it");
        await store.CompleteAsync(id, ThreadStatus.Failed, "Worker failed: compiler not found");

        var detail = await store.GetAsync(id);

        Assert.NotNull(detail);
        Assert.Equal(ThreadStatus.Failed, detail!.Thread.Status);
        Assert.Equal("error", detail.Entries[^1].Kind);
        Assert.True(detail.Entries[^1].Failed);
    }

    [Fact]
    public async Task The_thread_is_bounded()
    {
        using var database = TestDatabase.Create();
        var store = CreateStore(database);

        var id = await store.BeginAsync(Guid.NewGuid(), "Writer", "Writer", "long run");

        for (var line = 0; line < WorkerThreadStore.MaxEntriesPerThread + 12; line++)
        {
            await store.AppendAsync(id, ThreadEntryKind.Thought, "line " + line);
        }

        var detail = await store.GetAsync(id);

        Assert.NotNull(detail);
        Assert.Equal(WorkerThreadStore.MaxEntriesPerThread, detail!.Entries.Count);
        Assert.Equal("line 311", detail.Entries[^1].Text);
    }

    [Fact]
    public async Task The_hub_reports_every_change()
    {
        using var database = TestDatabase.Create();
        var hub = new WorkerThreadHub();
        var store = new WorkerThreadStore(database.Factory, hub);

        var seen = new List<Guid>();
        hub.Changed += seen.Add;

        var id = await store.BeginAsync(Guid.NewGuid(), "Writer", "Writer", "go");
        await store.AppendAsync(id, ThreadEntryKind.Thought, "step");
        await store.CompleteAsync(id, ThreadStatus.Completed, "done");

        Assert.True(seen.Count >= 3, $"expected at least three notifications, saw {seen.Count}");
        Assert.All(seen, thread => Assert.Equal(id, thread));
    }

    [Fact]
    public void A_broken_listener_cannot_break_the_run()
    {
        var hub = new WorkerThreadHub();
        hub.Changed += _ => throw new InvalidOperationException("ui is gone");

        hub.Notify(Guid.NewGuid());
    }

    [Fact]
    public async Task A_delegation_in_a_run_produces_a_thread_with_the_workers_steps()
    {
        using var database = TestDatabase.Create();
        using var workspace = new TempWorkspace();

        database.SeedAgents(TestAgents.Ceo(), TestAgents.Worker("Forge", "Software Engineer", tools: true));

        var hub = new WorkerThreadHub();
        var store = new WorkerThreadStore(database.Factory, hub);

        var gateway = new FakeLlmGateway
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
                    return FakeLlmGateway.Text("CEO final.");
                }

                return call.Messages.OfType<LlmToolMessage>().Any()
                    ? FakeLlmGateway.Text("Wrote the file.")
                    : new LlmReply(
                        null,
                        new[] { new LlmToolCallRequest("c1", "write_file", """{"filePath":"greet.py","content":"print('hi')"}""") },
                        "ToolCalls");
            }
        };

        database.SeedWorkspace(workspace.Root);
        database.SeedConnection();

        var workspaceService = new WorkspaceService(database.Factory);
        var tools = new ToolService(workspaceService, NullLogger<ToolService>.Instance);

        var orchestrator = new AgentOrchestrator(
            database.Factory,
            gateway,
            tools,
            workspaceService,
            database.Setup(),
            MsOptions.Create(new OmniRouteOptions()),
            NullLogger<AgentOrchestrator>.Instance,
            new ApprovalCoordinator(),
            store);

        await orchestrator.RunAsync("Write a python hello world");

        var threads = await store.ListRecentAsync();
        Assert.Single(threads);
        Assert.Equal("Forge", threads[0].AgentName);
        Assert.Equal(ThreadStatus.Completed, threads[0].Status);

        var detail = await store.GetAsync(threads[0].Id);
        Assert.NotNull(detail);

        var kinds = detail!.Entries.Select(entry => entry.Kind).ToList();

        Assert.Equal("instruction", kinds[0]);
        Assert.Contains("tool_call", kinds);
        Assert.Contains("tool_result", kinds);
        Assert.Equal("output", kinds[^1]);
        Assert.Contains(detail.Entries, entry => entry.ToolName == "write_file");

        // The tool really ran: the file is on disk and the agent's chat says so.
        Assert.True(workspace.Exists("greet.py"));
        Assert.Contains(detail.Entries, entry => entry.Kind == "tool_result" && entry.Text.Contains("greet.py", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Without_a_store_a_run_still_works()
    {
        // --self-test and the existing tests construct the orchestrator without threads; that must
        // stay a supported configuration.
        using var database = TestDatabase.Create();
        using var workspace = new TempWorkspace();

        database.SeedAgents(TestAgents.Ceo(), TestAgents.Worker("Scout", "Researcher"));

        var gateway = new FakeLlmGateway
        {
            Responder = call => call.Messages.OfType<LlmSystemMessage>().Any(system =>
                system.Text.Contains(AgentOrchestrator.PlanningContractMarker, StringComparison.Ordinal))
                ? FakeLlmGateway.Text("""[{"role":"Researcher","instructions":"look"}]""")
                : FakeLlmGateway.Text("done")
        };

        database.SeedWorkspace(workspace.Root);
        database.SeedConnection();

        var workspaceService = new WorkspaceService(database.Factory);
        var tools = new ToolService(workspaceService, NullLogger<ToolService>.Instance);

        var orchestrator = new AgentOrchestrator(
            database.Factory,
            gateway,
            tools,
            workspaceService,
            database.Setup(),
            MsOptions.Create(new OmniRouteOptions()),
            NullLogger<AgentOrchestrator>.Instance);

        var task = await orchestrator.RunAsync("do something");

        Assert.False(string.IsNullOrWhiteSpace(task.FinalOutput));
        Assert.Empty(await database.NewContext().AgentThreads.ToListAsync());
    }
}
