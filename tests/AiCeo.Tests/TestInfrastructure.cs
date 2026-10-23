using AiCeo.Data;
using AiCeo.Models;
using AiCeo.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AiCeo.Tests;

/// <summary>An isolated SQLite database (in memory) for each test.</summary>
internal sealed class TestDatabase : IDisposable
{
    private TestDatabase(SqliteConnection connection, AppDbContext db)
    {
        Connection = connection;
        Db = db;
    }

    public SqliteConnection Connection { get; }

    public AppDbContext Db { get; }

    public static TestDatabase Create()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        var db = new AppDbContext(options);
        db.Database.EnsureCreated();

        return new TestDatabase(connection, db);
    }

    public TestDatabase SeedAgents(params Agent[] agents)
    {
        Db.Agents.AddRange(agents);
        Db.SaveChanges();
        return this;
    }

    public void Dispose()
    {
        Db.Dispose();
        Connection.Dispose();
    }
}

/// <summary>
/// Records every LLM call so the tests can assert ordering, isolation and concurrency.
/// </summary>
internal sealed class FakeLlmGateway : ILlmGateway
{
    private readonly object _gate = new();
    private readonly List<LlmCall> _calls = new();
    private int _active;

    public int MaxConcurrentCalls { get; private set; }

    public Func<LlmCall, string> Responder { get; set; } =
        call => call.SystemPrompt.Contains(AgentOrchestrator.PlanningContractMarker, StringComparison.Ordinal)
            ? "[{\"role\":\"Researcher\",\"instructions\":\"Research the topic\"}]"
            : $"output from {call.AgentName}";

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

    public async Task<string> CompleteAsync(LlmCall call, CancellationToken cancellationToken = default)
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
            await Task.Delay(10, cancellationToken);
            return Responder(call);
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

    public static Agent Worker(string name, string role) => new()
    {
        Name = name,
        Role = role,
        SystemPrompt = $"You are {name} the {role}.",
        ModelId = "worker-model",
        Provider = LlmProvider.OmniRoute,
        IsManager = false
    };
}
