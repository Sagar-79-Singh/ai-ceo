using AiCeo.Data;
using AiCeo.Models;
using AiCeo.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AiCeo.Tests;

/// <summary>An isolated SQLite database (in memory) plus the factory the services expect.</summary>
internal sealed class TestDatabase : IDisposable
{
    private TestDatabase(SqliteConnection connection, DbContextOptions<AppDbContext> options)
    {
        Connection = connection;
        Options = options;

        using var db = new AppDbContext(options);
        db.Database.EnsureCreated();
    }

    public SqliteConnection Connection { get; }

    public DbContextOptions<AppDbContext> Options { get; }

    public IDbContextFactory<AppDbContext> Factory => new TestDbContextFactory(Options);

    public AppDbContext NewContext() => new(Options);

    public TestDatabase SeedAgents(params Agent[] agents)
    {
        using var db = NewContext();
        db.Agents.AddRange(agents);
        db.SaveChanges();
        return this;
    }

    /// <summary>Serialises a workspace row exactly like the UI does.</summary>
    public TestDatabase SeedWorkspace(string? rootPath, bool toolsEnabled = true)
    {
        using var db = NewContext();
        db.WorkspaceSettings.Add(new WorkspaceSettings
        {
            Id = WorkspaceSettings.SingletonId,
            RootPath = rootPath,
            ToolsEnabled = toolsEnabled,
            UpdatedAt = DateTime.UtcNow
        });
        db.SaveChanges();
        return this;
    }

    public void Dispose() => Connection.Dispose();

    private sealed class TestDbContextFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;

        public TestDbContextFactory(DbContextOptions<AppDbContext> options) => _options = options;

        public AppDbContext CreateDbContext() => new(_options);
    }

    public static TestDatabase Create()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        return new TestDatabase(connection, options);
    }
}

/// <summary>A throwaway folder that plays the role of the user's workspace.</summary>
internal sealed class TempWorkspace : IDisposable
{
    public TempWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "aiceo-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Write(string relativePath, string content)
    {
        var path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public string Read(string relativePath) => File.ReadAllText(Path.Combine(Root, relativePath));

    public bool Exists(string relativePath) => File.Exists(Path.Combine(Root, relativePath));

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup is best effort.
        }
    }
}

/// <summary>
/// Records every LLM call and replies from a scripted list, so the tool loop, ordering and
/// concurrency can be asserted without a network.
/// </summary>
internal sealed class FakeLlmGateway : ILlmGateway
{
    private readonly object _gate = new();
    private readonly List<LlmCall> _calls = new();
    private readonly Queue<Func<LlmCall, LlmReply>> _script = new();
    private int _active;

    public int MaxConcurrentCalls { get; private set; }

    public Func<LlmCall, LlmReply>? Responder { get; set; }

    public IReadOnlyList<LlmCall> Calls
    {
        get
        {
            lock (_gate)
            {
                return _calls.ToList();
            }
        }
    }

    /// <summary>Queues one reply (used to script a multi-round tool conversation).</summary>
    public FakeLlmGateway Then(Func<LlmCall, LlmReply> reply)
    {
        lock (_gate)
        {
            _script.Enqueue(reply);
        }

        return this;
    }

    public FakeLlmGateway ThenText(string text) => Then(_ => LlmReplyText(text));

    public FakeLlmGateway ThenToolCalls(params LlmToolCallRequest[] calls)
        => Then(_ => new LlmReply(null, calls, "ToolCalls"));

    public FakeLlmGateway ThenPlan(string json)
        => Then(call => call.Tools.Count == 0 && call.Messages.Any(message =>
                message is LlmSystemMessage system
                && system.Text.Contains(AgentOrchestrator.PlanningContractMarker, StringComparison.Ordinal))
            ? LlmReplyText(json)
            : Text($"agent reply for {call.AgentName}"));

    public static LlmReply LlmReplyText(string text) => new(text, Array.Empty<LlmToolCallRequest>(), "Stop");

    public static LlmReply Text(string text) => LlmReplyText(text);

    public async Task<LlmReply> CompleteAsync(LlmCall call, CancellationToken cancellationToken = default)
    {
        var active = Interlocked.Increment(ref _active);
        MaxConcurrentCalls = Math.Max(MaxConcurrentCalls, active);

        lock (_gate)
        {
            _calls.Add(call);
        }

        try
        {
            // A real provider takes time; this delay makes accidental parallelism observable.
            await Task.Delay(5, cancellationToken);

            Func<LlmCall, LlmReply>? next = null;
            lock (_gate)
            {
                if (_script.Count > 0)
                {
                    next = _script.Dequeue();
                }
            }

            if (next is not null)
            {
                return next(call);
            }

            if (Responder is not null)
            {
                return Responder(call);
            }

            return LlmReplyText($"agent reply for {call.AgentName}");
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }

    public Task<ProviderHealth> CheckAsync(LlmCall probe, CancellationToken cancellationToken = default)
        => Task.FromResult(new ProviderHealth(true, "ok", probe.Provider.ToString(), probe.BaseUrl, probe.ModelId));
}

internal static class TestAgents
{
    public static Agent Ceo(string name = "Atlas", LlmProvider provider = LlmProvider.OmniRoute, string? apiKey = null) => new()
    {
        Name = name,
        Role = "Chief Executive Officer",
        SystemPrompt = "You are the CEO.",
        ModelId = "ceo-model",
        Provider = provider,
        ApiKey = apiKey,
        IsManager = true
    };

    public static Agent Worker(string name, string role, bool tools = false) => new()
    {
        Name = name,
        Role = role,
        SystemPrompt = $"You are {name} the {role}.",
        ModelId = "worker-model",
        Provider = LlmProvider.OmniRoute,
        IsManager = false,
        CanUseTools = tools
    };
}
