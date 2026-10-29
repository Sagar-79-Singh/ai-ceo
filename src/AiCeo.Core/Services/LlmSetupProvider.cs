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
    private volatile LlmAuthState _auth = LlmAuthState.PasteKey;

    public LlmSetupProvider(IDbContextFactory<AppDbContext> factory)
    {
        _factory = factory;
    }

    /// <summary>The connection in effect. Never null; <see cref="LlmSetup.Unconfigured"/> before setup.</summary>
    public LlmSetup Current => _current;

    /// <summary>True once an API key and a model are stored.</summary>
    public bool IsConfigured => _current.IsConfigured;

    /// <summary>v2.0.1: where the key came from (a pasted key, GitHub Copilot or ChatGPT).</summary>
    public LlmAuthState Auth => _auth;

    /// <summary>True when the current key was issued by a device-flow sign-in.</summary>
    public bool IsSignedIn => _auth.IsDeviceFlow && _current.HasApiKey;

    /// <summary>Reads the stored connection. Safe to call repeatedly; never throws for a missing row.</summary>
    public async Task<LlmSetup> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);

        var settings = await db.UserSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == UserSettings.SingletonId, cancellationToken);

        _current = settings?.ToSetup().Normalize() ?? LlmSetup.Unconfigured;
        _auth = settings?.ToAuth() ?? LlmAuthState.PasteKey;

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

        // Typing or pasting a key replaces whatever a device flow had stored: the key is what the
        // next run will use, so the sign-in badge has to go with it.
        settings.ApplyAuth(LlmAuthState.PasteKey);

        await db.SaveChangesAsync(cancellationToken);

        _current = normalized;
        _auth = LlmAuthState.PasteKey;
        return normalized;
    }

    /// <summary>
    /// v2.0.1 — stores a token issued by a device flow, together with who it belongs to.
    ///
    /// The token lands in the same column a pasted key uses, so nothing downstream (the gateway, the
    /// agents, the health probe) needs to know the difference. Only the badge and the settings screen
    /// read <see cref="Auth"/>.
    /// </summary>
    public async Task<LlmSetup> SaveSignedInAsync(
        LlmSetup setup,
        LlmAuthState auth,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auth);

        if (!auth.IsDeviceFlow)
        {
            throw new ArgumentException("SaveSignedInAsync only stores a device-flow token.", nameof(auth));
        }

        var normalized = setup.Normalize();

        if (!normalized.HasApiKey)
        {
            throw new ArgumentException("The sign-in did not return a token.", nameof(setup));
        }

        await using var db = await _factory.CreateDbContextAsync(cancellationToken);

        var settings = await db.UserSettings
            .FirstOrDefaultAsync(row => row.Id == UserSettings.SingletonId, cancellationToken);

        if (settings is null)
        {
            settings = UserSettings.Defaults();
            db.UserSettings.Add(settings);
        }

        settings.ApplySetup(normalized);
        settings.ApplyAuth(auth);
        await db.SaveChangesAsync(cancellationToken);

        _current = normalized;
        _auth = auth;
        return normalized;
    }

    /// <summary>Forgets a device-flow token; the model and provider stay configured.</summary>
    public async Task<LlmSetup> SignOutAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);

        var settings = await db.UserSettings
            .FirstOrDefaultAsync(row => row.Id == UserSettings.SingletonId, cancellationToken);

        if (settings is null)
        {
            _auth = LlmAuthState.PasteKey;
            return _current;
        }

        settings.ApiKey = null;
        settings.ApplyAuth(LlmAuthState.PasteKey);
        await db.SaveChangesAsync(cancellationToken);

        _current = settings.ToSetup().Normalize();
        _auth = LlmAuthState.PasteKey;
        return _current;
    }

    /// <summary>Forgets the in-memory value (tests and shutdown).</summary>
    public void Clear() => _current = LlmSetup.Unconfigured;
}
