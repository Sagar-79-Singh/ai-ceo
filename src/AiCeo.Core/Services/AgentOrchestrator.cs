using System.Text;
using System.Text.Json;
using AiCeo.Data;
using AiCeo.Models;
using AiCeo.Options;
using AiCeo.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiCeo.Services;

/// <summary>
/// The custom orchestrator (no agent framework, no Python, no Node) implementing a strict
/// star topology:
/// user -> CEO -> worker -> CEO -> worker -> ... -> CEO -> user.
///
/// Workers never see each other's raw conversations, workers run strictly sequentially, and only
/// workers with <see cref="Agent.CanUseTools"/> get the workspace tools. Those workers run an
/// agentic loop (model -> tool -> model -> …) until they produce a final answer.
/// </summary>
public sealed class AgentOrchestrator
{
    private const int MaxContextCharsPerWorker = 1500;

    /// <summary>How many model/tool round trips a tool-enabled worker may take.</summary>
    public const int MaxToolRounds = 8;

    /// <summary>Hard cap on tool executions for one worker, across all rounds.</summary>
    public const int MaxToolCallsPerWorker = 24;

    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly ILlmGateway _llm;
    private readonly ToolService _tools;
    private readonly WorkspaceService _workspace;
    private readonly LlmSetupProvider _setup;
    private readonly OmniRouteOptions _options;
    private readonly ILogger<AgentOrchestrator> _logger;

    public AgentOrchestrator(
        IDbContextFactory<AppDbContext> factory,
        ILlmGateway llm,
        ToolService tools,
        WorkspaceService workspace,
        LlmSetupProvider setup,
        IOptions<OmniRouteOptions> options,
        ILogger<AgentOrchestrator> logger)
    {
        _factory = factory;
        _llm = llm;
        _tools = tools;
        _workspace = workspace;
        _setup = setup;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Stable fragment of the planning contract, used by tests to identify the plan call.</summary>
    public const string PlanningContractMarker = "Respond with ONLY a JSON array";

    /// <summary>Stable fragment of the synthesis contract.</summary>
    public const string SynthesisContractMarker = "writing the final answer for the user";

    /// <summary>Stable fragment of the worker tool contract.</summary>
    public const string ToolContractMarker = "You have direct access to the user's workspace";

    private const string PlanningContract =
        """
        You are the Chief Executive Officer in a strict star topology.

        Rules of your role:
        1. The user talks only to you. You are the only agent the user ever sees.
        2. You break the user's goal into a plan of 1-6 sequential assignments and delegate each one
           to exactly one worker from the roster below. Workers cannot talk to each other and cannot
           create new tasks.
        3. Each assignment must be self-contained and concrete: say what to produce, the format, and
           any constraints. Workers only see the overall goal and your assignment text.
        4. Never do the workers' job for them in the plan; assign it. If a worker has a workspace
           tool bench, tell them to build, write or verify files with it.

        Respond with ONLY a JSON array (no prose, no markdown fences) in exactly this shape:
        [{"role":"<worker role from the roster>","instructions":"<complete assignment for that worker>"}]

        Use the roster exactly as written. Never invent roles that are not listed.
        """;

    private const string WorkerContract =
        """
        You are a worker agent inside a strict star topology.

        Rules of your role:
        1. You report to the CEO only. The user never talks to you directly.
        2. Execute the assignment you are given. Do not ask clarifying questions, do not delegate,
           do not spawn new tasks and do not talk to other workers.
        3. Return finished work, not a plan. Be specific, structured and concise. If something is
           genuinely unknowable, state the assumption you made and continue.
        """;

    private const string ToolWorkerContract =
        """
        You are a worker agent inside a strict star topology with a real workspace on the user's disk.

        Rules of your role:
        1. You report to the CEO only. The user never talks to you directly.
        2. Work iteratively with your tools: inspect the workspace, write or edit the files that are
           needed, run builds/tests, read the errors, and fix them. Keep going until the assignment
           is genuinely complete.
        3. Prefer relative paths. Every file and command is confined to the workspace root; anything
           outside is refused, so never try to escape it.
        4. When you are done, reply with plain text summarising what you created or changed (file
           paths, commands run, and any remaining problems) and nothing else. Do not emit tool
           calls in your final answer.
        """;

    private const string SynthesisContract =
        """
        You are the Chief Executive Officer writing the final answer for the user.

        You have already delegated work to your team. Synthesize their results into one coherent,
        decision-ready answer:
        - Lead with the direct answer, not with process commentary.
        - Use short sections or bullets when it helps scanning.
        - Resolve conflicts between workers and say which finding you trust and why.
        - Never mention internal JSON, log ids, or that you are a language model.
        If a worker failed, acknowledge the gap and deliver the best answer possible from what you have.
        """;

    /// <summary>
    /// Runs one full CEO cycle and streams every step to <paramref name="onLog"/> as it happens.
    /// Execution is strictly sequential.
    /// </summary>
    public async Task<TaskItem> RunAsync(
        string userPrompt,
        Func<TaskLog, Task>? onLog = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userPrompt))
        {
            throw new ArgumentException("The prompt cannot be empty.", nameof(userPrompt));
        }

        var prompt = userPrompt.Trim();
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);

        var agents = await db.Agents.AsNoTracking().ToListAsync(cancellationToken);
        var ceo = agents.FirstOrDefault(agent => agent.IsManager)
                  ?? throw new InvalidOperationException(
                      "No CEO agent is configured. Mark one agent as the manager in the sidebar.");

        var workers = agents
            .Where(agent => !agent.IsManager && agent.Id != ceo.Id)
            .OrderBy(agent => agent.Name)
            .ToList();

        // v1.0.16: fail before a single token is spent, with a message the UI can show verbatim.
        // There is no fallback key or default model in the build any more, so an unconfigured
        // install has to be sent to the setup screen rather than to a provider.
        var setup = _setup.Current;
        if (!setup.IsConfigured && string.IsNullOrWhiteSpace(ceo.ApiKey))
        {
            throw new LlmException(
                "No provider connection is configured yet. Enter your provider, base URL, API key and "
                + "model in the setup screen — the whole team uses those values.");
        }

        var task = new TaskItem
        {
            Id = Guid.NewGuid(),
            UserPrompt = prompt,
            CreatedAt = DateTime.UtcNow
        };

        db.Tasks.Add(task);
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            // ---- 1. Plan ----------------------------------------------------------------
            var plan = await BuildPlanAsync(db, ceo, workers, prompt, task, onLog, cancellationToken);
            task.CeoPlan = JsonSerializer.Serialize(plan);
            await db.SaveChangesAsync(cancellationToken);

            // ---- 2. Sequential delegation ------------------------------------------------
            var results = new List<WorkerResult>();
            var workspaceRoot = await _workspace.GetRootAsync(cancellationToken);

            foreach (var step in plan)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var worker = ResolveWorker(workers, step.Role);
                if (worker is null)
                {
                    await LogAsync(
                        db,
                        task,
                        step.Role.Length > 0 ? step.Role : "Unassigned",
                        LogActions.Error,
                        $"No worker matches the role '{step.Role}'. The CEO assignment was skipped.",
                        onLog,
                        cancellationToken);
                    continue;
                }

                await LogAsync(db, task, worker.Name, LogActions.Delegated, step.Instructions, onLog, cancellationToken);

                try
                {
                    var output = worker.CanUseTools && workspaceRoot is not null
                        ? await RunToolWorkerAsync(db, task, worker, workspaceRoot, prompt, step, results, onLog, cancellationToken)
                        : await RunPlainWorkerAsync(task, worker, prompt, step, results, onLog, db, cancellationToken);

                    results.Add(new WorkerResult(worker.Name, worker.Role, step.Instructions, output));

                    await LogAsync(db, task, worker.Name, LogActions.Result, output, onLog, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception, "Worker {Worker} failed", worker.Name);
                    await LogAsync(
                        db,
                        task,
                        worker.Name,
                        LogActions.Error,
                        $"Worker failed: {exception.Message}",
                        onLog,
                        cancellationToken);
                }
            }

            // ---- 3. CEO synthesis --------------------------------------------------------
            var final = await SynthesizeAsync(db, ceo, prompt, plan, results, task, onLog, cancellationToken);
            task.FinalOutput = final;
            await db.SaveChangesAsync(cancellationToken);

            return task;
        }
        catch (OperationCanceledException)
        {
            task.FinalOutput = "Run cancelled before the CEO produced a final answer.";
            db.Tasks.Update(task);
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    // -----------------------------------------------------------------------------------------
    // Workers
    // -----------------------------------------------------------------------------------------

    /// <summary>A worker without tools: one round trip, done.</summary>
    private async Task<string> RunPlainWorkerAsync(
        TaskItem task,
        Agent worker,
        string prompt,
        PlanStep step,
        IReadOnlyList<WorkerResult> previousResults,
        Func<TaskLog, Task>? onLog,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var connection = LlmConnections.Resolve(worker, _setup.Current, _options);

        var messages = new List<LlmMessage>
        {
            new LlmSystemMessage($"{worker.SystemPrompt.Trim()}\n\n{WorkerContract}"),
            new LlmUserMessage(ComposeWorkerPrompt(prompt, step, previousResults))
        };

        var reply = await _llm.CompleteAsync(
            new LlmCall(
                worker.Name,
                connection.Provider,
                connection.BaseUrl,
                connection.ApiKey,
                connection.ModelId,
                messages,
                Array.Empty<LlmToolDefinition>()),
            cancellationToken);

        // A tool-less worker may still hallucinate a tool call; ignore it and take its text.
        return string.IsNullOrWhiteSpace(reply.Text)
            ? "(the worker returned no text)"
            : reply.Text!;
    }

    /// <summary>
    /// A tool-enabled worker: model -> tools -> model, sequentially, until it answers in text or
    /// the round/call budget is exhausted.
    /// </summary>
    private async Task<string> RunToolWorkerAsync(
        AppDbContext db,
        TaskItem task,
        Agent worker,
        string workspaceRoot,
        string prompt,
        PlanStep step,
        IReadOnlyList<WorkerResult> previousResults,
        Func<TaskLog, Task>? onLog,
        CancellationToken cancellationToken)
    {
        var connection = LlmConnections.Resolve(worker, _setup.Current, _options);
        var tools = ToolCatalog.Definitions;

        var systemPrompt = $"""
            {worker.SystemPrompt.Trim()}

            {ToolWorkerContract}

            {ToolContractMarker}: the folder
            "{workspaceRoot}"
            Use relative paths such as "src/Program.cs". Files are created if missing.
            """;

        var messages = new List<LlmMessage>
        {
            new LlmSystemMessage(systemPrompt),
            new LlmUserMessage(ComposeWorkerPrompt(prompt, step, previousResults))
        };

        var toolCallsUsed = 0;
        var finalText = new StringBuilder();

        for (var round = 1; round <= MaxToolRounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var reply = await _llm.CompleteAsync(
                new LlmCall(
                    worker.Name,
                    connection.Provider,
                    connection.BaseUrl,
                    connection.ApiKey,
                    connection.ModelId,
                    messages,
                    tools),
                cancellationToken);

            if (reply.Text is not null)
            {
                finalText.AppendLine(reply.Text);
            }

            if (!reply.WantsTools)
            {
                return finalText.Length > 0
                    ? finalText.ToString().Trim()
                    : "(the worker finished without a summary)";
            }

            messages.Add(new LlmAssistantMessage(reply.Text, reply.ToolCalls));

            foreach (var call in reply.ToolCalls)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (toolCallsUsed >= MaxToolCallsPerWorker)
                {
                    await LogAsync(
                        db,
                        task,
                        worker.Name,
                        LogActions.Error,
                        $"Tool budget exhausted ({MaxToolCallsPerWorker} calls). The worker must summarise now.",
                        onLog,
                        cancellationToken);

                    messages.Add(new LlmToolMessage(
                        call.Id,
                        ToolResult.Failure(call.Name, $"Tool budget exhausted after {MaxToolCallsPerWorker} calls.").ToToolContent()));

                    continue;
                }

                toolCallsUsed++;
                await LogAsync(db, task, worker.Name, LogActions.Tool, call.ArgumentsJson, onLog, cancellationToken, call.Name);

                var result = await _tools.ExecuteAsync(call.Name, call.ArgumentsJson, cancellationToken);

                await LogAsync(
                    db,
                    task,
                    worker.Name,
                    LogActions.ToolResult,
                    result.Ok ? result.Content : $"ERROR: {result.Error}",
                    onLog,
                    cancellationToken,
                    call.Name);

                messages.Add(new LlmToolMessage(call.Id, result.ToToolContent()));
            }

            // Keep the transcript bounded: the system prompt plus the most recent tool round.
            TrimTranscript(messages);
        }

        // Budget spent: force a text-only wrap-up so the CEO still gets a result.
        await LogAsync(
            db,
            task,
            worker.Name,
            LogActions.Error,
            $"Reached the {MaxToolRounds}-round tool limit; asking the worker to summarise.",
            onLog,
            cancellationToken);

        var wrapUp = await _llm.CompleteAsync(
            new LlmCall(
                worker.Name,
                connection.Provider,
                connection.BaseUrl,
                connection.ApiKey,
                connection.ModelId,
                messages.Concat(new LlmMessage[]
                {
                    new LlmUserMessage(
                        "Stop using tools now. Reply with a plain-text summary of what you completed, "
                        + "which files you changed, and anything still broken.")
                }).ToList(),
                Array.Empty<LlmToolDefinition>()),
            cancellationToken);

        if (wrapUp.Text is not null)
        {
            finalText.AppendLine(wrapUp.Text);
        }

        return finalText.Length > 0
            ? finalText.ToString().Trim()
            : "(the worker hit the tool-round limit without a summary)";
    }

    /// <summary>
    /// Drops old assistant/tool rounds so long tool sessions stay inside the context window, while
    /// never orphaning a tool message from the assistant message that requested it.
    /// </summary>
    private static void TrimTranscript(List<LlmMessage> messages)
    {
        const int keepToolMessages = 12;

        var toolIndices = messages
            .Select((message, index) => (message, index))
            .Where(entry => entry.message is LlmToolMessage)
            .Select(entry => entry.index)
            .ToList();

        if (toolIndices.Count <= keepToolMessages)
        {
            return;
        }

        var firstKeptTool = toolIndices[toolIndices.Count - keepToolMessages];

        // Walk back to the assistant turn that owns that tool message, so the pair stays intact.
        var cut = firstKeptTool;
        while (cut > 2 && messages[cut - 1] is LlmToolMessage)
        {
            cut--;
        }

        if (cut > 2 && messages[cut - 1] is LlmAssistantMessage)
        {
            cut--;
        }

        // Never remove the leading system + user message.
        if (cut <= 2)
        {
            return;
        }

        messages.RemoveRange(2, cut - 2);
    }

    // -----------------------------------------------------------------------------------------
    // CEO
    // -----------------------------------------------------------------------------------------

    private async Task<List<PlanStep>> BuildPlanAsync(
        AppDbContext db,
        Agent ceo,
        IReadOnlyList<Agent> workers,
        string prompt,
        TaskItem task,
        Func<TaskLog, Task>? onLog,
        CancellationToken cancellationToken)
    {
        var roster = workers.Count == 0
            ? "(no workers are available)"
            : string.Join(
                "\n",
                workers.Select(worker =>
                    $"- role: \"{worker.Role}\" (agent name: {worker.Name}"
                    + $"{(worker.CanUseTools ? ", has workspace tools: files + terminal" : string.Empty)})"));

        var systemPrompt = $"{ceo.SystemPrompt.Trim()}\n\n{PlanningContract}\n\nAvailable worker roster:\n{roster}";

        string raw;
        try
        {
            var connection = LlmConnections.Resolve(ceo, _setup.Current, _options);

            var reply = await _llm.CompleteAsync(
                new LlmCall(
                    ceo.Name,
                    connection.Provider,
                    connection.BaseUrl,
                    connection.ApiKey,
                    connection.ModelId,
                    new LlmMessage[]
                    {
                        new LlmSystemMessage(systemPrompt),
                        new LlmUserMessage($"User goal:\n{prompt}")
                    },
                    Array.Empty<LlmToolDefinition>()),
                cancellationToken);

            raw = reply.Text ?? string.Empty;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await LogAsync(db, task, ceo.Name, LogActions.Error, $"Planning call failed: {exception.Message}", onLog, cancellationToken);

            var degraded = workers.Count == 0
                ? new List<PlanStep>()
                : new List<PlanStep> { new() { Role = workers[0].Role, Instructions = prompt } };

            await LogAsync(db, task, ceo.Name, LogActions.Plan, JsonSerializer.Serialize(degraded), onLog, cancellationToken);
            return degraded;
        }

        if (TaskPlanParser.TryParse(raw, out var plan) && plan.Count > 0)
        {
            await LogAsync(db, task, ceo.Name, LogActions.Plan, JsonSerializer.Serialize(plan), onLog, cancellationToken);
            return plan;
        }

        // ---- Graceful degradation ------------------------------------------------------------
        _logger.LogWarning("CEO plan could not be parsed. Raw output: {Raw}", raw);
        await LogAsync(
            db,
            task,
            ceo.Name,
            LogActions.Error,
            "The CEO's plan was not valid JSON, so the orchestrator fell back to a single delegation. "
            + "Raw output was:\n" + Truncate(raw, 1200),
            onLog,
            cancellationToken);

        if (workers.Count == 0)
        {
            return new List<PlanStep>();
        }

        var fallback = new List<PlanStep>
        {
            new() { Role = workers[0].Role, Instructions = prompt }
        };

        await LogAsync(db, task, ceo.Name, LogActions.Plan, JsonSerializer.Serialize(fallback), onLog, cancellationToken);
        return fallback;
    }

    private async Task<string> SynthesizeAsync(
        AppDbContext db,
        Agent ceo,
        string prompt,
        IReadOnlyList<PlanStep> plan,
        IReadOnlyList<WorkerResult> results,
        TaskItem task,
        Func<TaskLog, Task>? onLog,
        CancellationToken cancellationToken)
    {
        var context = new StringBuilder();
        context.AppendLine($"User goal:\n{prompt}\n");
        context.AppendLine("Your plan:");
        for (var index = 0; index < plan.Count; index++)
        {
            context.AppendLine($"{index + 1}. [{plan[index].Role}] {plan[index].Instructions}");
        }

        context.AppendLine();
        context.AppendLine("Delegation results (sequential):");

        if (results.Count == 0)
        {
            context.AppendLine("(no worker produced a result)");
        }
        else
        {
            foreach (var result in results)
            {
                context.AppendLine($"--- {result.AgentName} ({result.Role}) ---");
                context.AppendLine(result.Output);
                context.AppendLine();
            }
        }

        var systemPrompt = $"{ceo.SystemPrompt.Trim()}\n\n{SynthesisContract}";

        try
        {
            var connection = LlmConnections.Resolve(ceo, _setup.Current, _options);

            var reply = await _llm.CompleteAsync(
                new LlmCall(
                    ceo.Name,
                    connection.Provider,
                    connection.BaseUrl,
                    connection.ApiKey,
                    connection.ModelId,
                    new LlmMessage[]
                    {
                        new LlmSystemMessage(systemPrompt),
                        new LlmUserMessage(context.ToString())
                    },
                    Array.Empty<LlmToolDefinition>()),
                cancellationToken);

            if (!string.IsNullOrWhiteSpace(reply.Text))
            {
                return reply.Text!;
            }

            throw new LlmException(connection.Provider, connection.BaseUrl, connection.ModelId, "the CEO returned an empty final answer.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "CEO synthesis failed");
            await LogAsync(
                db,
                task,
                ceo.Name,
                LogActions.Error,
                $"Synthesis failed: {exception.Message}",
                onLog,
                cancellationToken);

            if (results.Count == 0)
            {
                return "The CEO could not complete this run. " + exception.Message;
            }

            // Still give the user the raw work rather than nothing.
            var fallback = new StringBuilder();
            fallback.AppendLine("The CEO could not synthesize a final answer, so here are the raw worker results:");
            fallback.AppendLine();
            foreach (var result in results)
            {
                fallback.AppendLine($"## {result.AgentName} ({result.Role})");
                fallback.AppendLine(result.Output);
                fallback.AppendLine();
            }

            return fallback.ToString().Trim();
        }
    }

    // -----------------------------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------------------------

    private static string ComposeWorkerPrompt(string goal, PlanStep step, IReadOnlyList<WorkerResult> previousResults)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Overall goal");
        builder.AppendLine(goal);
        builder.AppendLine();
        builder.AppendLine("# Your assignment from the CEO");
        builder.AppendLine(step.Instructions);

        if (previousResults.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("# Work already completed by other workers");
            foreach (var previous in previousResults)
            {
                builder.AppendLine($"## {previous.AgentName} ({previous.Role})");
                builder.AppendLine(Truncate(previous.Output, MaxContextCharsPerWorker));
                builder.AppendLine();
            }
        }

        builder.AppendLine();
        builder.AppendLine("Deliver your finished result now. Do not ask follow-up questions.");
        return builder.ToString();
    }

    private static Agent? ResolveWorker(IReadOnlyList<Agent> workers, string role)
    {
        if (workers.Count == 0)
        {
            return null;
        }

        var wanted = (role ?? string.Empty).Trim();
        if (wanted.Length == 0)
        {
            return workers.Count == 1 ? workers[0] : null;
        }

        var exact = workers.FirstOrDefault(worker =>
            string.Equals(worker.Role, wanted, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        var byName = workers.FirstOrDefault(worker =>
            string.Equals(worker.Name, wanted, StringComparison.OrdinalIgnoreCase));
        if (byName is not null)
        {
            return byName;
        }

        var partial = workers.FirstOrDefault(worker =>
            worker.Role.Contains(wanted, StringComparison.OrdinalIgnoreCase)
            || wanted.Contains(worker.Role, StringComparison.OrdinalIgnoreCase));
        if (partial is not null)
        {
            return partial;
        }

        return workers.Count == 1 ? workers[0] : null;
    }

    private static async Task LogAsync(
        AppDbContext db,
        TaskItem task,
        string agentName,
        string action,
        string content,
        Func<TaskLog, Task>? onLog,
        CancellationToken cancellationToken,
        string? toolName = null)
    {
        var log = new TaskLog
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            AgentName = string.IsNullOrWhiteSpace(agentName) ? "System" : agentName.Trim(),
            Action = action,
            Content = content ?? string.Empty,
            ToolName = toolName,
            CreatedAt = DateTime.UtcNow
        };

        db.TaskLogs.Add(log);
        await db.SaveChangesAsync(cancellationToken);

        if (onLog is not null)
        {
            await onLog(log);
        }
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength] + "\n… (truncated)";
    }
}
