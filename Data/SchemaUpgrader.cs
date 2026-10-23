using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace AiCeo.Data;

/// <summary>
/// v1.0.2 added <c>Provider</c>, <c>BaseUrl</c> and <c>ApiKey</c> to <c>Agents</c>. Databases
/// created by v1.0.x already exist on disk, so the new columns are added in place instead of
/// requiring users to delete their data. Runs on every start and is a no-op once applied.
/// </summary>
public static class SchemaUpgrader
{
    private static readonly (string Column, string Ddl)[] AgentColumns =
    {
        ("Provider", "ALTER TABLE \"Agents\" ADD COLUMN \"Provider\" TEXT NOT NULL DEFAULT 'OmniRoute'"),
        ("BaseUrl", "ALTER TABLE \"Agents\" ADD COLUMN \"BaseUrl\" TEXT NULL"),
        ("ApiKey", "ALTER TABLE \"Agents\" ADD COLUMN \"ApiKey\" TEXT NULL")
    };

    public static async Task UpgradeAsync(AppDbContext db, ILogger logger, CancellationToken cancellationToken = default)
    {
        var connection = db.Database.GetDbConnection();
        var openedHere = false;

        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
            openedHere = true;
        }

        try
        {
            var existing = await ReadColumnsAsync(connection, "Agents", cancellationToken);
            if (existing.Count == 0)
            {
                // No Agents table yet: EnsureCreated will build the current schema.
                return;
            }

            foreach (var (column, ddl) in AgentColumns)
            {
                if (existing.Contains(column))
                {
                    continue;
                }

                await ExecuteAsync(connection, ddl, cancellationToken);
                logger.LogInformation("Schema upgrade: added Agents.{Column}", column);
            }
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }
    }

    /// <summary>Column names of a table, empty when the table does not exist.</summary>
    public static async Task<HashSet<string>> ReadColumnsAsync(
        DbConnection connection,
        string table,
        CancellationToken cancellationToken = default)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info('{table.Replace("'", "''")}');";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!await reader.IsDBNullAsync(1, cancellationToken))
            {
                columns.Add(reader.GetString(1));
            }
        }

        return columns;
    }

    private static async Task ExecuteAsync(DbConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
