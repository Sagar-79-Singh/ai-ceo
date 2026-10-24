using AiCeo.Data;
using AiCeo.Models;
using Microsoft.EntityFrameworkCore;

namespace AiCeo.Services;

/// <summary>
/// Owns the single <see cref="UserSettings"/> row: the theme, accent colour and font size the
/// desktop shell paints with. Every read falls back to sensible defaults, so a fresh database or a
/// hand-edited file can never leave the UI unstyled.
/// </summary>
public sealed class UserSettingsService
{
    private readonly IDbContextFactory<AppDbContext> _factory;

    /// <summary>Raised after a save so listeners (the shell) can repaint immediately.</summary>
    public event EventHandler<UserSettingsSnapshot>? Changed;

    public UserSettingsService(IDbContextFactory<AppDbContext> factory)
    {
        _factory = factory;
    }

    /// <summary>Current preferences; creates the row on first use.</summary>
    public async Task<UserSettingsSnapshot> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var settings = await GetOrCreateAsync(db, cancellationToken);
        return Sanitize(settings).ToSnapshot();
    }

    /// <summary>
    /// Persists a complete set of preferences and notifies listeners. Invalid enum values are
    /// clamped to the defaults rather than thrown, so a corrupt row cannot brick the UI.
    /// </summary>
    public async Task<UserSettingsSnapshot> SaveAsync(
        UserSettingsSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        var cleaned = Sanitize(new UserSettings
        {
            Theme = snapshot.Theme,
            AccentColor = snapshot.AccentColor,
            FontSize = snapshot.FontSize
        });

        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var settings = await GetOrCreateAsync(db, cancellationToken);

        settings.Theme = cleaned.Theme;
        settings.AccentColor = cleaned.AccentColor;
        settings.FontSize = cleaned.FontSize;
        settings.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken);

        var result = cleaned.ToSnapshot();
        Changed?.Invoke(this, result);
        return result;
    }

    /// <summary>Restores the defaults (System theme, green accent, medium text).</summary>
    public Task<UserSettingsSnapshot> ResetAsync(CancellationToken cancellationToken = default)
        => SaveAsync(UserSettingsSnapshot.Default, cancellationToken);

    /// <summary>The theme tokens for the saved preferences.</summary>
    public async Task<ThemePalette> GetPaletteAsync(bool systemPrefersDark, CancellationToken cancellationToken = default)
        => UiTheme.Build(await GetAsync(cancellationToken), systemPrefersDark);

    private static async Task<UserSettings> GetOrCreateAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var settings = await db.UserSettings
            .FirstOrDefaultAsync(row => row.Id == UserSettings.SingletonId, cancellationToken);

        if (settings is not null)
        {
            return settings;
        }

        settings = UserSettings.Defaults();
        db.UserSettings.Add(settings);
        await db.SaveChangesAsync(cancellationToken);
        return settings;
    }

    private static UserSettings Sanitize(UserSettings settings)
    {
        if (!Enum.IsDefined(settings.Theme))
        {
            settings.Theme = AppTheme.System;
        }

        if (!Enum.IsDefined(settings.AccentColor))
        {
            settings.AccentColor = AccentColor.Green;
        }

        if (!Enum.IsDefined(settings.FontSize))
        {
            settings.FontSize = FontSizePreference.Medium;
        }

        return settings;
    }
}
