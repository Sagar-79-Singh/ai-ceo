using AiCeo.Data;
using AiCeo.Models;
using Microsoft.EntityFrameworkCore;

namespace AiCeo.Services;

/// <summary>
/// Owns the single workspace row in SQLite: which folder the agents may touch, and whether
/// tools are enabled at all.
/// </summary>
public sealed class WorkspaceService
{
    private readonly IDbContextFactory<AppDbContext> _factory;

    public WorkspaceService(IDbContextFactory<AppDbContext> factory)
    {
        _factory = factory;
    }

    public async Task<WorkspaceView> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var settings = await GetOrCreateAsync(db, cancellationToken);
        return ToView(settings);
    }

    /// <summary>
    /// Stores the folder chosen through the native picker. <c>null</c> clears the workspace.
    /// </summary>
    public async Task<WorkspaceView> SetPathAsync(string? folderPath, CancellationToken cancellationToken = default)
    {
        string? normalized = null;

        if (!string.IsNullOrWhiteSpace(folderPath))
        {
            var trimmed = folderPath.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!Path.IsPathRooted(trimmed))
            {
                throw new ArgumentException("The workspace folder must be an absolute path.");
            }

            if (!Directory.Exists(trimmed))
            {
                throw new ArgumentException($"The folder '{trimmed}' does not exist.");
            }

            normalized = Path.GetFullPath(trimmed);
        }

        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var settings = await GetOrCreateAsync(db, cancellationToken);

        settings.RootPath = normalized;
        settings.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        return ToView(settings);
    }

    public async Task<WorkspaceView> SetToolsEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var settings = await GetOrCreateAsync(db, cancellationToken);

        settings.ToolsEnabled = enabled;
        settings.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        return ToView(settings);
    }

    /// <summary>The folder agents may touch, or <c>null</c> when unset, missing, or disabled.</summary>
    public async Task<string?> GetRootAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var settings = await db.WorkspaceSettings.AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == WorkspaceSettings.SingletonId, cancellationToken);

        if (settings is null || !settings.ToolsEnabled || string.IsNullOrWhiteSpace(settings.RootPath))
        {
            return null;
        }

        return Directory.Exists(settings.RootPath) ? settings.RootPath : null;
    }

    private static async Task<WorkspaceSettings> GetOrCreateAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var settings = await db.WorkspaceSettings
            .FirstOrDefaultAsync(row => row.Id == WorkspaceSettings.SingletonId, cancellationToken);

        if (settings is not null)
        {
            return settings;
        }

        settings = new WorkspaceSettings { Id = WorkspaceSettings.SingletonId };
        db.WorkspaceSettings.Add(settings);
        await db.SaveChangesAsync(cancellationToken);
        return settings;
    }

    private static WorkspaceView ToView(WorkspaceSettings settings)
    {
        var path = string.IsNullOrWhiteSpace(settings.RootPath) ? null : settings.RootPath;
        var exists = path is not null && Directory.Exists(path);

        var problem = path switch
        {
            null => null,
            _ when !exists => "The selected folder no longer exists. Pick it again.",
            _ when !settings.ToolsEnabled => "Tools are turned off.",
            _ => null
        };

        return new WorkspaceView(path, path is not null && exists, exists, settings.ToolsEnabled, problem);
    }
}
