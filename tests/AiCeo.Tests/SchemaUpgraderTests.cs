using AiCeo.Data;
using AiCeo.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiCeo.Tests;

public class SchemaUpgraderTests
{
    /// <summary>A v1.0.x database: no Provider/BaseUrl/ApiKey columns.</summary>
    private const string LegacySchema = """
        CREATE TABLE "Agents" (
            "Id" TEXT NOT NULL CONSTRAINT "PK_Agents" PRIMARY KEY,
            "Name" TEXT NOT NULL,
            "Role" TEXT NOT NULL,
            "SystemPrompt" TEXT NOT NULL,
            "ModelId" TEXT NOT NULL,
            "IsManager" INTEGER NOT NULL
        );
        INSERT INTO "Agents" ("Id","Name","Role","SystemPrompt","ModelId","IsManager")
        VALUES ('11111111-1111-1111-1111-111111111111','Atlas','CEO','You are the CEO.','gh/gpt-5.4-nano',1);
        """;

    [Fact]
    public async Task Adds_the_provider_columns_to_a_v1_database_and_keeps_the_data()
    {
        await using var legacy = await LegacyDatabase.CreateAsync();

        var before = await SchemaUpgrader.ReadColumnsAsync(legacy.Connection, "Agents");
        Assert.DoesNotContain("Provider", before);
        Assert.DoesNotContain("BaseUrl", before);
        Assert.DoesNotContain("ApiKey", before);

        await SchemaUpgrader.UpgradeAsync(legacy.Db, NullLogger.Instance);

        var after = await SchemaUpgrader.ReadColumnsAsync(legacy.Connection, "Agents");
        Assert.Contains("Provider", after);
        Assert.Contains("BaseUrl", after);
        Assert.Contains("ApiKey", after);

        // The existing row is readable by the current model and defaults to OmniRoute.
        var agent = await legacy.Db.Agents.AsNoTracking().SingleAsync();
        Assert.Equal("Atlas", agent.Name);
        Assert.Equal(LlmProvider.OmniRoute, agent.Provider);
        Assert.Null(agent.BaseUrl);
        Assert.Null(agent.ApiKey);
    }

    [Fact]
    public async Task Is_idempotent()
    {
        await using var legacy = await LegacyDatabase.CreateAsync();

        await SchemaUpgrader.UpgradeAsync(legacy.Db, NullLogger.Instance);
        await SchemaUpgrader.UpgradeAsync(legacy.Db, NullLogger.Instance);
        await SchemaUpgrader.UpgradeAsync(legacy.Db, NullLogger.Instance);

        var columns = await SchemaUpgrader.ReadColumnsAsync(legacy.Connection, "Agents");
        Assert.Single(columns.Where(column => column.Equals("Provider", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(6, columns.Count);
    }

    [Fact]
    public async Task Does_nothing_when_the_agents_table_does_not_exist()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);

        await SchemaUpgrader.UpgradeAsync(db, NullLogger.Instance);

        Assert.Empty(await SchemaUpgrader.ReadColumnsAsync(connection, "Agents"));
    }

    /// <summary>Owns the connection and the context so disposal order is deterministic.</summary>
    private sealed class LegacyDatabase : IAsyncDisposable
    {
        private LegacyDatabase(SqliteConnection connection, AppDbContext db)
        {
            Connection = connection;
            Db = db;
        }

        public SqliteConnection Connection { get; }

        public AppDbContext Db { get; }

        public static async Task<LegacyDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            await using (var command = connection.CreateCommand())
            {
                command.CommandText = LegacySchema;
                await command.ExecuteNonQueryAsync();
            }

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .Options;

            return new LegacyDatabase(connection, new AppDbContext(options));
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
