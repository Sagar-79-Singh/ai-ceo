using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AiCeo.Data;

/// <summary>
/// Brings an existing SQLite file up to the current schema without losing data:
/// <list type="bullet">
///   <item>v1.0.1 → v1.0.2: <c>Agents.Provider</c>, <c>Agents.BaseUrl</c>, <c>Agents.ApiKey</c></item>
///   <item>v1.0.2 → v1.0.3: <c>Agents.CanUseTools</c>, <c>TaskLogs.ToolName</c>, and the
///   <c>WorkspaceSettings</c> table</item>
///   <item>v1.0.4 → v1.0.5: the <c>UserSettings</c> table (theme, accent colour, font size)</item>
///   <item>v1.0.15 → v1.0.16: <c>UserSettings.Provider</c>, <c>BaseUrl</c>, <c>ApiKey</c> and
///   <c>ModelId</c> — the LLM connection the first-run setup stores</item>
/// </list>
/// Runs on every start and is a no-op once applied.
/// </summary>
public static class SchemaUpgrader
{
    private static readonly (string Column, string Ddl)[] AgentColumns =
    {
        ("Provider", "ALTER TABLE \"Agents\" ADD COLUMN \"Provider\" TEXT NOT NULL DEFAULT 'OmniRoute'"),
        ("BaseUrl", "ALTER TABLE \"Agents\" ADD COLUMN \"BaseUrl\" TEXT NULL"),
        ("ApiKey", "ALTER TABLE \"Agents\" ADD COLUMN \"ApiKey\" TEXT NULL"),
        ("CanUseTools", "ALTER TABLE \"Agents\" ADD COLUMN \"CanUseTools\" INTEGER NOT NULL DEFAULT 0")
    };

    private static readonly (string Column, string Ddl)[] TaskLogColumns =
    {
        ("ToolName", "ALTER TABLE \"TaskLogs\" ADD COLUMN \"ToolName\" TEXT NULL")
    };

    private const string WorkspaceTableDdl = """
        CREATE TABLE IF NOT EXISTS "WorkspaceSettings" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_WorkspaceSettings" PRIMARY KEY AUTOINCREMENT,
            "RootPath" TEXT NULL,
            "ToolsEnabled" INTEGER NOT NULL DEFAULT 1,
            "UpdatedAt" TEXT NOT NULL
        );
        """;

    private const string UserSettingsTableDdl = """
        CREATE TABLE IF NOT EXISTS "UserSettings" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_UserSettings" PRIMARY KEY AUTOINCREMENT,
            "Theme" TEXT NOT NULL DEFAULT 'System',
            "AccentColor" TEXT NOT NULL DEFAULT 'Green',
            "FontSize" TEXT NOT NULL DEFAULT 'Medium',
            "Provider" TEXT NOT NULL DEFAULT 'OmniRoute',
            "BaseUrl" TEXT NULL,
            "ApiKey" TEXT NULL,
            "ModelId" TEXT NULL,
            "UpdatedAt" TEXT NOT NULL
        );
        """;

    /// <summary>v1.0.16: the LLM connection columns, added to an existing row without touching it.</summary>
    private static readonly (string Column, string Ddl)[] UserSettingsColumns =
    {
        ("Provider", "ALTER TABLE \"UserSettings\" ADD COLUMN \"Provider\" TEXT NOT NULL DEFAULT 'OmniRoute'"),
        ("BaseUrl", "ALTER TABLE \"UserSettings\" ADD COLUMN \"BaseUrl\" TEXT NULL"),
        ("ApiKey", "ALTER TABLE \"UserSettings\" ADD COLUMN \"ApiKey\" TEXT NULL"),
        ("ModelId", "ALTER TABLE \"UserSettings\" ADD COLUMN \"ModelId\" TEXT NULL")
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
            var agentColumns = await ReadColumnsAsync(connection, "Agents", cancellationToken);
            if (agentColumns.Count == 0)
            {
                // Fresh database: EnsureCreated builds the current schema.
                return;
            }

            await AddMissingAsync(connection, "Agents", AgentColumns, agentColumns, logger, cancellationToken);

            var logColumns = await ReadColumnsAsync(connection, "TaskLogs", cancellationToken);
            if (logColumns.Count > 0)
            {
                await AddMissingAsync(connection, "TaskLogs", TaskLogColumns, logColumns, logger, cancellationToken);
            }

            if (!await TableExistsAsync(connection, "WorkspaceSettings", cancellationToken))
            {
                await ExecuteAsync(connection, WorkspaceTableDdl, cancellationToken);
                logger.LogInformation("Schema upgrade: created WorkspaceSettings table");
            }

            if (!await TableExistsAsync(connection, "UserSettings", cancellationToken))
            {
                await ExecuteAsync(connection, UserSettingsTableDdl, cancellationToken);
                logger.LogInformation("Schema upgrade: created UserSettings table");
            }
            else
            {
                var settingsColumns = await ReadColumnsAsync(connection, "UserSettings", cancellationToken);
                await AddMissingAsync(connection, "UserSettings", UserSettingsColumns, settingsColumns, logger, cancellationToken);
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

    private static async Task AddMissingAsync(
        DbConnection connection,
        string table,
        (string Column, string Ddl)[] required,
        HashSet<string> existing,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        foreach (var (column, ddl) in required)
        {
            if (existing.Contains(column))
            {
                continue;
            }

            await ExecuteAsync(connection, ddl, cancellationToken);
            logger.LogInformation("Schema upgrade: added {Table}.{Column}", table, column);
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

    public static async Task<bool> TableExistsAsync(
        DbConnection connection,
        string table,
        CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";

        var parameter = command.CreateParameter();
        parameter.ParameterName = "$name";
        parameter.Value = table;
        command.Parameters.Add(parameter);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result switch
        {
            long value => value > 0,
            int value => value > 0,
            _ => false
        };
    }

    private static async Task ExecuteAsync(DbConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
