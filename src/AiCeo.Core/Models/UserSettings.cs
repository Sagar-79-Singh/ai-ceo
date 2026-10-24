namespace AiCeo.Models;

/// <summary>How the UI picks its colour scheme.</summary>
public enum AppTheme
{
    /// <summary>Follow the operating system (default).</summary>
    System = 0,

    /// <summary>Always dark.</summary>
    Dark = 1,

    /// <summary>Always light.</summary>
    Light = 2
}

/// <summary>The accent used for primary actions, badges and focus rings.</summary>
public enum AccentColor
{
    Green = 0,
    Blue = 1,
    Purple = 2,
    Amber = 3,
    Rose = 4
}

/// <summary>UI text scale.</summary>
public enum FontSizePreference
{
    Small = 0,
    Medium = 1,
    Large = 2
}

/// <summary>
/// The single row of UI preferences (id 1). Stored in SQLite next to the agents so the choice
/// survives restarts, and re-applied to the Avalonia resource dictionary on every launch.
/// </summary>
public class UserSettings
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    public AppTheme Theme { get; set; } = AppTheme.System;

    public AccentColor AccentColor { get; set; } = AccentColor.Green;

    public FontSizePreference FontSize { get; set; } = FontSizePreference.Medium;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public UserSettingsSnapshot ToSnapshot() => new(Theme, AccentColor, FontSize);

    public static UserSettings Defaults() => new();
}

/// <summary>Immutable, UI-friendly copy of the preferences.</summary>
public sealed record UserSettingsSnapshot(
    AppTheme Theme,
    AccentColor AccentColor,
    FontSizePreference FontSize)
{
    public static UserSettingsSnapshot Default { get; } = new(AppTheme.System, AccentColor.Green, FontSizePreference.Medium);
}
