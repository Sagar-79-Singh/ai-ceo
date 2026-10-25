using AiCeo.Data;
using AiCeo.Models;
using Microsoft.EntityFrameworkCore;

namespace AiCeo.Services;

/// <summary>
/// Owns the user's LLM connection (provider, base URL, API key, model).
///
/// It is a singleton so the orchestrator, the health probe and the directory all resolve the exact
/// same values the setup screen saved, without a database round trip on every LLM call:
/// <list type="bullet">
///   <item><see cref="Current"/> is the last known value — safe to read from any thread;</item>
///   <item><see cref="RefreshAsync"/> loads it from the <c>UserSettings</c> row (called once at
///   start-up, after seeding);</item>
///   <item><see cref="SaveAsync"/> validates, writes the four columns and publishes the new value so
///   the next run — and the next health probe — uses it immediately.</item>
/// </list>
/// v1.0.16: this is what replaces the hardcoded key and model the app used to ship with. A fresh
/// install has <see cref="LlmSetup.Unconfigured"/> here, and the UI blocks until it does not.
/// </summary>
public sealed class LlmSetupProvider
{
    private readonly IDbContextFactory<AppDbContext> _factory;

    private volatile LlmSetup _current = LlmSetup.Unconfigured;

    public LlmSetupProvider(IDbContextFactory<AppDbContext> factory)
    {
        _factory = factory;
    }

    /// <summary>The connection in effect. Never null; <see cref="LlmSetup.Unconfigured"/> before setup.</summary>
    public LlmSetup Current => _current;

    /// <summary>True once an API key and a model are stored.</summary>
    public bool IsConfigured => _current.IsConfigured;

    /// <summary>Reads the stored connection. Safe to call repeatedly; never throws for a missing row.</summary>
    public async Task<LlmSetup> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);

        var settings = await db.UserSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == UserSettings.SingletonId, cancellationToken);

        _current = settings?.ToSetup().Normalize() ?? LlmSetup.Unconfigured;
        return _current;
    }

    /// <summary>
    /// Validates and stores the connection the user typed, then publishes it. Throws
    /// <see cref="ArgumentException"/> with a message that can be shown in the dialog verbatim.
    /// </summary>
    public async Task<LlmSetup> SaveAsync(LlmSetup setup, CancellationToken cancellationToken = default)
    {
        var normalized = setup.Validate();

        await using var db = await _factory.CreateDbContextAsync(cancellationToken);

        var settings = await db.UserSettings
            .FirstOrDefaultAsync(row => row.Id == UserSettings.SingletonId, cancellationToken);

        if (settings is null)
        {
            settings = UserSettings.Defaults();
            db.UserSettings.Add(settings);
        }

        settings.ApplySetup(normalized);
        await db.SaveChangesAsync(cancellationToken);

        _current = normalized;
        return normalized;
    }

    /// <summary>Forgets the in-memory value (tests and shutdown).</summary>
    public void Clear() => _current = LlmSetup.Unconfigured;
}
