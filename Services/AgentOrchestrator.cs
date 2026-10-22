using System.Text;
using System.Text.Json;
using AiCeo.Data;
using AiCeo.Models;
using Microsoft.EntityFrameworkCore;

namespace AiCeo.Services;

/// <summary>
/// The custom orchestrator (no agent framework, no Python, no Node) implementing a strict
/// star topology:
/// user -> CEO -> worker -> CEO -> worker -> ... -> CEO -> user.
/// Workers never see each other's raw conversations, never run in parallel, and never delegate.
/// </summary>
public sealed class AgentOrchestrator
{
    private const int MaxContextCharsPerWorker = 1500;

    private readonly AppDbContext _db;
    private readonly OmniRouteChatService _llm;
    private readonly ILogger<AgentOrchestrator> _logger;

    public AgentOrchestrator(AppDbContext db, OmniRouteChatService llm, ILogger<AgentOrchestrator> logger)
    {
        _db = db;
        _llm = llm;
        _logger = logger;
    }

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
        4. Never do the workers' job for them in the plan; assign it.

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
    /// Execution is strictly sequential (RULES.md rule 5).
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

        var agents = await _db.Agents.AsNoTracking().ToListAsync(cancellationToken);
        var ceo = agents.FirstOrDefault(agent => agent.IsManager)
                  ?? throw new InvalidOperationException(
                      "No CEO agent is configured. Mark one agent as the manager in the sidebar.");

        var workers = agents
            .Where(agent => !agent.IsManager && agent.Id != ceo.Id)
            .OrderBy(agent => agent.Name)
            .ToList();

        var task = new TaskItem
        {
            Id = Guid.NewGuid(),
            UserPrompt = prompt,
            CreatedAt = DateTime.UtcNow
        };

        _db.Tasks.Add(task);
        await _db.SaveChangesAsync(cancellationToken);

        try
        {
            // ---- 1. Plan ----------------------------------------------------------------
            var plan = await BuildPlanAsync(ceo, workers, prompt, task, onLog, cancellationToken);
            task.CeoPlan = JsonSerializer.Serialize(plan);
            await _db.SaveChangesAsync(cancellationToken);

            // ---- 2. Sequential delegation ------------------------------------------------
            var results = new List<WorkerResult>();

            foreach (var step in plan)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var worker = ResolveWorker(workers, step.Role);
                if (worker is null)
                {
                    await LogAsync(
                        task,
                        step.Role.Length > 0 ? step.Role : "Unassigned",
                        "error",
                        $"No worker matches the role '{step.Role}'. The CEO assignment was skipped.",
                        onLog,
                        cancellationToken);
                    continue;
                }

                await LogAsync(task, worker.Name, "delegated", step.Instructions, onLog, cancellationToken);

                try
                {
                    var output = await _llm.CompleteAsync(
                        new ChatRequest(
                            worker.ModelId,
                            ComposeWorkerSystemPrompt(worker),
                            ComposeWorkerPrompt(prompt, step, results)),
                        cancellationToken);

                    results.Add(new WorkerResult(worker.Name, worker.Role, step.Instructions, output));

                    await LogAsync(task, worker.Name, "result", output, onLog, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception, "Worker {Worker} failed", worker.Name);
                    await LogAsync(
                        task,
                        worker.Name,
                        "error",
                        $"Worker failed: {exception.Message}",
                        onLog,
                        cancellationToken);
                }
            }

            // ---- 3. CEO synthesis --------------------------------------------------------
            var final = await SynthesizeAsync(ceo, prompt, plan, results, task, onLog, cancellationToken);
            task.FinalOutput = final;
            await _db.SaveChangesAsync(cancellationToken);

            return task;
        }
        catch (OperationCanceledException)
        {
            task.FinalOutput = "Run cancelled before the CEO produced a final answer.";
            _db.Tasks.Update(task);
            await _db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task<List<PlanStep>> BuildPlanAsync(
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
                workers.Select(worker => $"- role: \"{worker.Role}\" (agent name: {worker.Name})"));

        var systemPrompt = $"{ceo.SystemPrompt.Trim()}\n\n{PlanningContract}\n\nAvailable worker roster:\n{roster}";

        string raw;
        try
        {
            raw = await _llm.CompleteAsync(
                new ChatRequest(ceo.ModelId, systemPrompt, $"User goal:\n{prompt}"),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await LogAsync(task, ceo.Name, "error", $"Planning call failed: {exception.Message}", onLog, cancellationToken);

            var degraded = workers.Count == 0
                ? new List<PlanStep>()
                : new List<PlanStep> { new() { Role = workers[0].Role, Instructions = prompt } };

            await LogAsync(task, ceo.Name, "plan", JsonSerializer.Serialize(degraded), onLog, cancellationToken);
            return degraded;
        }

        if (TaskPlanParser.TryParse(raw, out var plan) && plan.Count > 0)
        {
            await LogAsync(task, ceo.Name, "plan", JsonSerializer.Serialize(plan), onLog, cancellationToken);
            return plan;
        }

        // ---- Graceful degradation (RULES.md rule 4) --------------------------------------
        _logger.LogWarning("CEO plan could not be parsed. Raw output: {Raw}", raw);
        await LogAsync(
            task,
            ceo.Name,
            "error",
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

        await LogAsync(task, ceo.Name, "plan", JsonSerializer.Serialize(fallback), onLog, cancellationToken);
        return fallback;
    }

    private async Task<string> SynthesizeAsync(
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
            return await _llm.CompleteAsync(
                new ChatRequest(ceo.ModelId, systemPrompt, context.ToString()),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "CEO synthesis failed");
            await LogAsync(
                task,
                ceo.Name,
                "error",
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

    private static string ComposeWorkerSystemPrompt(Agent worker)
        => $"{worker.SystemPrompt.Trim()}\n\n{WorkerContract}";

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

    private async Task LogAsync(
        TaskItem task,
        string agentName,
        string action,
        string content,
        Func<TaskLog, Task>? onLog,
        CancellationToken cancellationToken)
    {
        var log = new TaskLog
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            AgentName = string.IsNullOrWhiteSpace(agentName) ? "System" : agentName.Trim(),
            Action = action,
            Content = content ?? string.Empty,
            CreatedAt = DateTime.UtcNow
        };

        _db.TaskLogs.Add(log);
        await _db.SaveChangesAsync(cancellationToken);

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
