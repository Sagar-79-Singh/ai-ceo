using AiCeo.Models;
using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;

namespace AiCeo.Desktop.Theme;

/// <summary>
/// Copies a <see cref="ThemePalette"/> into the application's resource table, which is the Avalonia
/// equivalent of writing CSS custom properties into <c>:root</c>.
///
/// Every style in <c>App.axaml</c> refers to these keys with <c>{DynamicResource …}</c>, so changing
/// the theme is a single dictionary write plus a repaint — no window reload.
/// </summary>
public static class ThemeManager
{
    /// <summary>Key holding the human-readable variant name (<c>Dark</c> / <c>Light</c>).</summary>
    public const string VariantLabelKey = "ThemeVariantName";

    private static EventHandler<ThemePalette>? _themeChanged;

    /// <summary>The palette currently applied, if the shell has painted itself yet.</summary>
    public static ThemePalette? Current { get; private set; }

    /// <summary>
    /// The preferences the user chose (including "System", which <see cref="Current"/> has already
    /// resolved). Used to repaint when the OS flips between light and dark.
    /// </summary>
    public static UserSettingsSnapshot? Requested { get; private set; }

    /// <summary>Raised after the resource table has been updated.</summary>
    public static event EventHandler<ThemePalette>? ThemeChanged
    {
        add => _themeChanged += value;
        remove => _themeChanged -= value;
    }

    /// <summary>True when the operating system currently asks for a dark UI.</summary>
    public static bool SystemPrefersDark
    {
        get
        {
            try
            {
                var settings = Application.Current?.PlatformSettings;
                if (settings is not null)
                {
                    return settings.GetColorValues().ThemeVariant == PlatformThemeVariant.Dark;
                }
            }
            catch (Exception exception) when (exception is NotSupportedException or InvalidOperationException)
            {
                // Headless / very old platforms: fall back to the dark default.
            }

            return true;
        }
    }

    /// <summary>Paints the application with <paramref name="palette"/> and notifies listeners.</summary>
    public static void Apply(ThemePalette palette)
    {
        var application = Application.Current;

        if (application is null)
        {
            return;
        }

        application.RequestedThemeVariant = palette.IsDark ? ThemeVariant.Dark : ThemeVariant.Light;

        foreach (var (token, hex) in palette.ColorValues)
        {
            application.Resources[token] = new SolidColorBrush(Color.Parse(hex));
        }

        foreach (var (token, size) in palette.FontValues)
        {
            application.Resources[token] = size;
        }

        application.Resources[VariantLabelKey] = palette.IsDark ? "Dark" : "Light";

        Current = palette;
        _themeChanged?.Invoke(null, palette);
    }

    /// <summary>Convenience overload: builds the palette for a snapshot, then applies it.</summary>
    public static ThemePalette Apply(UserSettingsSnapshot settings)
    {
        Requested = settings;
        var palette = UiTheme.Build(settings, SystemPrefersDark);
        Apply(palette);
        return palette;
    }

    /// <summary>
    /// Repaints if (and only if) the user asked to follow the operating system. Called when the
    /// platform reports a light/dark switch.
    /// </summary>
    public static void ReapplyIfFollowingSystem()
    {
        if (Requested is { Theme: AppTheme.System } requested)
        {
            Apply(requested);
        }
    }
}

/// <summary>
/// Resolves theme tokens into brushes for the few view models that expose a colour directly
/// (status dots, banner tints, tool output). Falls back to the dark palette if the shell has not
/// applied a theme yet, so a unit-test or a crash window still renders something sensible.
/// </summary>
public static class ThemeBrushes
{
    private static readonly IBrush Fallback = new SolidColorBrush(Color.Parse("#5EE0B0"));

    /// <summary>The brush registered under <paramref name="token"/>.</summary>
    public static IBrush Get(string token, string fallbackHex = "#5EE0B0")
    {
        if (Application.Current?.Resources.TryGetResource(token, null, out var value) == true)
        {
            switch (value)
            {
                case IBrush brush:
                    return brush;
                case Color color:
                    return new SolidColorBrush(color);
            }
        }

        try
        {
            return new SolidColorBrush(Color.Parse(fallbackHex));
        }
        catch (FormatException)
        {
            return Fallback;
        }
    }
}
