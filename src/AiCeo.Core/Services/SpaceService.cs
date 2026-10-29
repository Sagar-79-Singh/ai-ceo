using System.Text.Json;
using AiCeo.Data;
using AiCeo.Models;
using Microsoft.EntityFrameworkCore;

namespace AiCeo.Services;

/// <summary>
/// v2.0.0 — persistent workspaces ("Spaces").
///
/// Instead of remembering one folder, the app now remembers a set of named spaces: the name, the
/// folder, the conversation that happened inside it, and which one was open last. Everything lives
/// in the same SQLite file as the agents, so the window reopens exactly where the user left it —
/// same space, same folder, same feed.
///
/// The feed is stored line by line (<see cref="SpaceFeedEntry"/>) rather than as one opaque blob, so
/// a space can be listed with its message count and a future release can search it.
/// </summary>
public sealed class SpaceService
{
    /// <summary>Feeds are trimmed to the most recent lines so a space cannot grow without bound.</summary>
    public const int MaxFeedEntries = 400;

    public const int MaxNameLength = 60;

    private static readonly JsonSerializerOptions StepsOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IDbContextFactory<AppDbContext> _factory;

    public SpaceService(IDbContextFactory<AppDbContext> factory)
    {
        _factory = factory;
    }

    /// <summary>Every space, most recently used first.</summary>
    public async Task<IReadOnlyList<SpaceView>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);

        var rows = await db.Spaces.AsNoTracking()
            .OrderByDescending(space => space.IsActive)
            .ThenByDescending(space => space.UpdatedAt)
            .Select(space => new { Space = space, Count = space.Feed.Count })
            .ToListAsync(cancellationToken);

        return rows.Select(row => ToView(row.Space, row.Count)).ToList();
    }

    /// <summary>The space the window should reopen with, or <c>null</c> when none exists yet.</summary>
    public async Task<SpaceView?> GetActiveAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);

        var row = await db.Spaces.AsNoTracking()
            .Where(space => space.IsActive)
            .OrderByDescending(space => space.UpdatedAt)
            .Select(space => new { Space = space, Count = space.Feed.Count })
            .FirstOrDefaultAsync(cancellationToken);

        return row is null ? null : ToView(row.Space, row.Count);
    }

    /// <summary>
    /// Creates a space and makes it the active one. The name is required and unique; the folder may
    /// be omitted (the user can choose one later) but is validated when supplied.
    /// </summary>
    public async Task<SpaceView> CreateAsync(
        string name,
        string? folderPath,
        CancellationToken cancellationToken = default)
    {
        var cleanName = (name ?? string.Empty).Trim();

        if (cleanName.Length == 0)
        {
            throw new ArgumentException("A space needs a name, for example \"Python Game\".", nameof(name));
        }

        if (cleanName.Length > MaxNameLength)
        {
            throw new ArgumentException($"A space name can be at most {MaxNameLength} characters.", nameof(name));
        }

        var cleanFolder = NormalizeFolder(folderPath);

        await using var db = await _factory.CreateDbContextAsync(cancellationToken);

        var exists = await db.Spaces.AsNoTracking()
            .AnyAsync(space => space.Name.ToLower() == cleanName.ToLower(), cancellationToken);

        if (exists)
        {
            throw new ArgumentException($"A space called \"{cleanName}\" already exists.", nameof(name));
        }

        await DeactivateAllAsync(db, cancellationToken);

        var created = new Space
        {
            Name = cleanName,
            FolderPath = cleanFolder,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        db.Spaces.Add(created);
        await db.SaveChangesAsync(cancellationToken);

        return ToView(created, 0);
    }

    /// <summary>Makes <paramref name="id"/> the active space. Returns null when it no longer exists.</summary>
    public async Task<SpaceView?> ActivateAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);

        var target = await db.Spaces.FirstOrDefaultAsync(space => space.Id == id, cancellationToken);
        if (target is null)
        {
            return null;
        }

        await DeactivateAllAsync(db, cancellationToken);

        target.IsActive = true;
        target.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        var count = await db.SpaceFeedEntries.AsNoTracking()
            .CountAsync(entry => entry.SpaceId == id, cancellationToken);

        return ToView(target, count);
    }

    /// <summary>
    /// Points the space at a folder (the native picker's answer) or clears it with <c>null</c>.
    /// </summary>
    public async Task<SpaceView?> SetFolderAsync(
        Guid id,
        string? folderPath,
        CancellationToken cancellationToken = default)
    {
        var cleanFolder = NormalizeFolder(folderPath);

        await using var db = await _factory.CreateDbContextAsync(cancellationToken);

        var space = await db.Spaces.FirstOrDefaultAsync(row => row.Id == id, cancellationToken);
        if (space is null)
        {
            return null;
        }

        space.FolderPath = cleanFolder;
        space.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        var count = await db.SpaceFeedEntries.AsNoTracking()
            .CountAsync(entry => entry.SpaceId == id, cancellationToken);

        return ToView(space, count);
    }

    /// <summary>Renames a space, keeping its folder and conversation.</summary>
    public async Task<SpaceView?> RenameAsync(Guid id, string name, CancellationToken cancellationToken = default)
    {
        var cleanName = (name ?? string.Empty).Trim();

        if (cleanName.Length == 0 || cleanName.Length > MaxNameLength)
        {
            throw new ArgumentException($"A space name must be 1-{MaxNameLength} characters long.", nameof(name));
        }

        await using var db = await _factory.CreateDbContextAsync(cancellationToken);

        var space = await db.Spaces.FirstOrDefaultAsync(row => row.Id == id, cancellationToken);
        if (space is null)
        {
            return null;
        }

        var clash = await db.Spaces.AsNoTracking()
            .AnyAsync(row => row.Id != id && row.Name.ToLower() == cleanName.ToLower(), cancellationToken);

        if (clash)
        {
            throw new ArgumentException($"A space called \"{cleanName}\" already exists.", nameof(name));
        }

        space.Name = cleanName;
        space.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        var count = await db.SpaceFeedEntries.AsNoTracking()
            .CountAsync(entry => entry.SpaceId == id, cancellationToken);

        return ToView(space, count);
    }

    /// <summary>
    /// Deletes a space and its conversation. When the deleted space was the active one, the most
    /// recently used remaining space takes over so the window is never left without one.
    /// </summary>
    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);

        var space = await db.Spaces.FirstOrDefaultAsync(row => row.Id == id, cancellationToken);
        if (space is null)
        {
            return false;
        }

        var wasActive = space.IsActive;

        db.Spaces.Remove(space);
        await db.SaveChangesAsync(cancellationToken);

        if (wasActive)
        {
            var next = await db.Spaces
                .OrderByDescending(row => row.UpdatedAt)
                .FirstOrDefaultAsync(cancellationToken);

            if (next is not null)
            {
                next.IsActive = true;
                next.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
            }
        }

        return true;
    }

    /// <summary>
    /// Replaces a space's saved conversation with the feed that is on screen right now. Called after
    /// every run and whenever the user switches spaces.
    /// </summary>
    public async Task SaveConversationAsync(
        Guid id,
        string? lastPrompt,
        string? finalOutput,
        IEnumerable<SpaceFeedItem> feed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(feed);

        var items = feed.ToList();

        // Trim from the front so the most recent lines survive.
        if (items.Count > MaxFeedEntries)
        {
            items = items.Skip(items.Count - MaxFeedEntries).ToList();
        }

        await using var db = await _factory.CreateDbContextAsync(cancellationToken);

        var space = await db.Spaces.FirstOrDefaultAsync(row => row.Id == id, cancellationToken);
        if (space is null)
        {
            return;
        }

        var existing = await db.SpaceFeedEntries
            .Where(entry => entry.SpaceId == id)
            .ToListAsync(cancellationToken);

        db.SpaceFeedEntries.RemoveRange(existing);

        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];

            db.SpaceFeedEntries.Add(new SpaceFeedEntry
            {
                SpaceId = id,
                Ordinal = index,
                Kind = Clip(item.Kind, 32, "note"),
                AgentName = Clip(item.AgentName, 120, string.Empty),
                Text = item.Text ?? string.Empty,
                ToolName = string.IsNullOrWhiteSpace(item.ToolName) ? null : Clip(item.ToolName!, 64, null),
                Failed = item.Failed,
                Pending = item.Pending,
                Title = Clip(item.Title, 200, string.Empty),
                StepsJson = JsonSerializer.Serialize(item.Steps ?? Array.Empty<SpaceStep>(), StepsOptions),
                CreatedAt = DateTime.UtcNow
            });
        }

        space.LastPrompt = lastPrompt ?? string.Empty;
        space.FinalOutput = finalOutput ?? string.Empty;
        space.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Everything needed to repaint the window exactly as it was left.</summary>
    public async Task<SpaceSnapshot?> LoadConversationAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);

        var space = await db.Spaces.AsNoTracking().FirstOrDefaultAsync(row => row.Id == id, cancellationToken);
        if (space is null)
        {
            return null;
        }

        var entries = await db.SpaceFeedEntries.AsNoTracking()
            .Where(entry => entry.SpaceId == id)
            .OrderBy(entry => entry.Ordinal)
            .ToListAsync(cancellationToken);

        var feed = entries.Select(ToFeedItem).ToList();

        return new SpaceSnapshot(ToView(space, feed.Count), space.LastPrompt, space.FinalOutput, feed);
    }

    // ------------------------------------------------------------------------------------ helpers

    private static SpaceFeedItem ToFeedItem(SpaceFeedEntry entry)
        => new(
            entry.Kind,
            entry.AgentName,
            entry.Text,
            entry.ToolName,
            entry.Failed,
            entry.Pending,
            entry.Title,
            ParseSteps(entry.StepsJson));

    private static IReadOnlyList<SpaceStep> ParseSteps(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<SpaceStep>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<SpaceStep>>(json, StepsOptions) ?? new List<SpaceStep>();
        }
        catch (JsonException)
        {
            return Array.Empty<SpaceStep>();
        }
    }

    private static async Task DeactivateAllAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var active = await db.Spaces.Where(space => space.IsActive).ToListAsync(cancellationToken);

        foreach (var space in active)
        {
            space.IsActive = false;
        }

        if (active.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private static string? NormalizeFolder(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return null;
        }

        var trimmed = folderPath.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (!Path.IsPathRooted(trimmed))
        {
            throw new ArgumentException("The space folder must be an absolute path.", nameof(folderPath));
        }

        if (!Directory.Exists(trimmed))
        {
            throw new ArgumentException($"The folder '{trimmed}' does not exist.", nameof(folderPath));
        }

        return Path.GetFullPath(trimmed);
    }

    private static SpaceView ToView(Space space, int feedCount)
        => new(space.Id, space.Name, space.FolderPath, space.IsActive, feedCount, space.UpdatedAt);

    private static string Clip(string? value, int max, string? fallback)
    {
        if (string.IsNullOrEmpty(value))
        {
            return fallback ?? string.Empty;
        }

        return value.Length <= max ? value : value[..max];
    }
}
