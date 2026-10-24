using System.Globalization;

namespace AiCeo.Models;

/// <summary>
/// The design tokens the UI paints with, exposed exactly like CSS custom properties
/// (<c>--color-app-bg</c>, <c>--font-base</c>, …) so the desktop shell only has to copy a
/// dictionary into its resource table.
///
/// Keeping the palettes here — instead of in the view — means theme rules are unit-testable
/// without a UI toolkit, and one palette serves every desktop target.
/// </summary>
public static class UiTheme
{
    // ------------------------------------------------------------------ token names

    public const string AppBg = "AppBg";
    public const string SidebarBg = "SidebarBg";
    public const string SidebarBorder = "SidebarBorder";
    public const string PanelBg = "PanelBg";
    public const string PanelBorder = "PanelBorder";
    public const string CardBg = "CardBg";
    public const string CardBorder = "CardBorder";
    public const string AgentCardBg = "AgentCardBg";
    public const string AgentCardBorder = "AgentCardBorder";
    public const string FeedBg = "FeedBg";
    public const string FeedBorder = "FeedBorder";
    public const string ComposerBg = "ComposerBg";
    public const string ComposerBorder = "ComposerBorder";
    public const string EditorBg = "EditorBg";
    public const string EditorBorder = "EditorBorder";
    public const string OverlayBg = "OverlayBg";
    public const string InputBg = "InputBg";
    public const string InputBorder = "InputBorder";
    public const string InputText = "InputText";
    public const string TextPrimary = "TextPrimary";
    public const string TextBody = "TextBody";
    public const string TextSecondary = "TextSecondary";
    public const string TextMuted = "TextMuted";
    public const string TextDim = "TextDim";
    public const string Accent = "Accent";
    public const string AccentOn = "AccentOn";
    public const string AccentSoft = "AccentSoft";
    public const string AccentSoftText = "AccentSoftText";
    public const string AccentBorder = "AccentBorder";
    public const string Ok = "Ok";
    public const string Warn = "Warn";
    public const string Danger = "Danger";
    public const string DangerBg = "DangerBg";
    public const string DangerBorder = "DangerBorder";
    public const string ToolBg = "ToolBg";
    public const string ToolBorder = "ToolBorder";
    public const string ToolAccent = "ToolAccent";
    public const string UserBubbleBg = "UserBubbleBg";
    public const string UserBubbleBorder = "UserBubbleBorder";
    public const string FinalBg = "FinalBg";
    public const string FinalBorder = "FinalBorder";
    public const string BannerInfoBg = "BannerInfoBg";
    public const string BannerInfoBorder = "BannerInfoBorder";
    public const string BannerErrorBg = "BannerErrorBg";
    public const string BannerErrorBorder = "BannerErrorBorder";
    public const string BadgeCeoBg = "BadgeCeoBg";
    public const string BadgeToolsBg = "BadgeToolsBg";
    public const string BrandTileBg = "BrandTileBg";
    public const string StepIndexBg = "StepIndexBg";
    public const string ButtonBg = "ButtonBg";
    public const string ButtonBorder = "ButtonBorder";
    public const string ButtonText = "ButtonText";

    // ------------------------------------------------------------------ font tokens

    public const string FontBase = "FontBase";
    public const string FontTitle = "FontTitle";
    public const string FontSubtitle = "FontSubtitle";
    public const string FontLabel = "FontLabel";
    public const string FontTiny = "FontTiny";
    public const string FontMono = "FontMono";
    public const string FontBody = "FontBody";
    public const string FontButton = "FontButton";

    /// <summary>Every colour token the shell must provide.</summary>
    public static IReadOnlyList<string> ColorTokens { get; } = new[]
    {
        AppBg, SidebarBg, SidebarBorder, PanelBg, PanelBorder, CardBg, CardBorder, AgentCardBg,
        AgentCardBorder, FeedBg, FeedBorder, ComposerBg, ComposerBorder, EditorBg, EditorBorder,
        OverlayBg, InputBg, InputBorder, InputText, TextPrimary, TextBody, TextSecondary, TextMuted,
        TextDim, Accent, AccentOn, AccentSoft, AccentSoftText, AccentBorder, Ok, Warn, Danger,
        DangerBg, DangerBorder, ToolBg, ToolBorder, ToolAccent, UserBubbleBg, UserBubbleBorder,
        FinalBg, FinalBorder, BannerInfoBg, BannerInfoBorder, BannerErrorBg, BannerErrorBorder,
        BadgeCeoBg, BadgeToolsBg, BrandTileBg, StepIndexBg, ButtonBg, ButtonBorder, ButtonText
    };

    /// <summary>Every font-size token the shell must provide.</summary>
    public static IReadOnlyList<string> FontTokens { get; } = new[]
    {
        FontBase, FontTitle, FontSubtitle, FontLabel, FontTiny, FontMono, FontBody, FontButton
    };

    // ------------------------------------------------------------------ resolution

    /// <summary>
    /// Chooses the concrete variant. <see cref="AppTheme.System"/> follows the OS preference.
    /// </summary>
    public static AppTheme ResolveVariant(AppTheme requested, bool systemPrefersDark) => requested switch
    {
        AppTheme.Dark => AppTheme.Dark,
        AppTheme.Light => AppTheme.Light,
        _ => systemPrefersDark ? AppTheme.Dark : AppTheme.Light
    };

    /// <summary>Builds the complete token set for a snapshot (with System already resolved).</summary>
    public static ThemePalette Build(UserSettingsSnapshot settings, bool systemPrefersDark)
    {
        var variant = ResolveVariant(settings.Theme, systemPrefersDark);
        var isDark = variant != AppTheme.Light;

        var colors = isDark ? DarkColors() : LightColors();
        ApplyAccent(colors, settings.AccentColor, isDark);
        ApplyFonts(colors, settings.FontSize);

        return new ThemePalette(variant, isDark, settings.AccentColor, settings.FontSize, colors);
    }

    /// <summary>The accent as a #rrggbb string, for previews and swatches.</summary>
    public static string AccentHex(AccentColor accent) => accent switch
    {
        AccentColor.Blue => "#3B82F6",
        AccentColor.Purple => "#8B5CF6",
        AccentColor.Amber => "#F59E0B",
        AccentColor.Rose => "#F43F5E",
        _ => "#5EE0B0"
    };

    public static string AccentDisplayName(AccentColor accent) => accent switch
    {
        AccentColor.Blue => "Blue",
        AccentColor.Purple => "Purple",
        AccentColor.Amber => "Amber",
        AccentColor.Rose => "Rose",
        _ => "Green"
    };

    public static string ThemeDisplayName(AppTheme theme) => theme switch
    {
        AppTheme.Dark => "Dark",
        AppTheme.Light => "Light",
        _ => "System default"
    };

    public static string FontDisplayName(FontSizePreference size) => size switch
    {
        FontSizePreference.Small => "Small",
        FontSizePreference.Large => "Large",
        _ => "Medium"
    };

    /// <summary>Scale factor applied to every font token.</summary>
    public static double FontScale(FontSizePreference size) => size switch
    {
        FontSizePreference.Small => 0.9,
        FontSizePreference.Large => 1.15,
        _ => 1.0
    };

    // ------------------------------------------------------------------ palettes

    private static Dictionary<string, string> DarkColors() => new(StringComparer.Ordinal)
    {
        [AppBg] = "#070A10",
        [SidebarBg] = "#0B111A",
        [SidebarBorder] = "#161F2E",
        [PanelBg] = "#101825",
        [PanelBorder] = "#1C2635",
        [CardBg] = "#0F1622",
        [CardBorder] = "#1C2635",
        [AgentCardBg] = "#0D1520",
        [AgentCardBorder] = "#1A2434",
        [FeedBg] = "#080C13",
        [FeedBorder] = "#151E2C",
        [ComposerBg] = "#0D1420",
        [ComposerBorder] = "#1B2635",
        [EditorBg] = "#0E1622",
        [EditorBorder] = "#23303F",
        [OverlayBg] = "#C4000000",
        [InputBg] = "#0A1017",
        [InputBorder] = "#1E2A3A",
        [InputText] = "#E4EDF8",
        [TextPrimary] = "#EAF2FB",
        [TextBody] = "#C9D6E6",
        [TextSecondary] = "#8EA3BD",
        [TextMuted] = "#5E7186",
        [TextDim] = "#48586C",
        [Accent] = "#5EE0B0",
        [AccentOn] = "#04140F",
        [AccentSoft] = "#153A30",
        [AccentSoftText] = "#5EE0B0",
        [AccentBorder] = "#1D4A44",
        [Ok] = "#5EE0B0",
        [Warn] = "#FFC46B",
        [Danger] = "#FF9A9A",
        [DangerBg] = "#2A1417",
        [DangerBorder] = "#5A2A31",
        [ToolBg] = "#0D1522",
        [ToolBorder] = "#22304A",
        [ToolAccent] = "#9AA8FF",
        [UserBubbleBg] = "#14243A",
        [UserBubbleBorder] = "#20354F",
        [FinalBg] = "#0E1B1A",
        [FinalBorder] = "#1D4A44",
        [BannerInfoBg] = "#0F2430",
        [BannerInfoBorder] = "#1D4A56",
        [BannerErrorBg] = "#2A1417",
        [BannerErrorBorder] = "#5A2A31",
        [BadgeCeoBg] = "#153A30",
        [BadgeToolsBg] = "#1B2436",
        [BrandTileBg] = "#123A33",
        [StepIndexBg] = "#17222F",
        [ButtonBg] = "#16202E",
        [ButtonBorder] = "#22303F",
        [ButtonText] = "#DCE7F5"
    };

    private static Dictionary<string, string> LightColors() => new(StringComparer.Ordinal)
    {
        [AppBg] = "#F4F6FA",
        [SidebarBg] = "#FFFFFF",
        [SidebarBorder] = "#E2E8F0",
        [PanelBg] = "#FFFFFF",
        [PanelBorder] = "#E2E8F0",
        [CardBg] = "#FFFFFF",
        [CardBorder] = "#E5EAF1",
        [AgentCardBg] = "#F8FAFD",
        [AgentCardBorder] = "#E5EAF1",
        [FeedBg] = "#FFFFFF",
        [FeedBorder] = "#E5EAF1",
        [ComposerBg] = "#FFFFFF",
        [ComposerBorder] = "#DFE6EF",
        [EditorBg] = "#FFFFFF",
        [EditorBorder] = "#DCE3EC",
        [OverlayBg] = "#66000000",
        [InputBg] = "#F7F9FC",
        [InputBorder] = "#D7DEE8",
        [InputText] = "#14202E",
        [TextPrimary] = "#0F1A26",
        [TextBody] = "#26333F",
        [TextSecondary] = "#5A6B7D",
        [TextMuted] = "#7A8899",
        [TextDim] = "#93A0AF",
        [Accent] = "#0F9D74",
        [AccentOn] = "#FFFFFF",
        [AccentSoft] = "#E3F7F0",
        [AccentSoftText] = "#0F9D74",
        [AccentBorder] = "#BFE7DA",
        [Ok] = "#0F9D74",
        [Warn] = "#B4690E",
        [Danger] = "#C0392B",
        [DangerBg] = "#FDECEA",
        [DangerBorder] = "#F3C4BE",
        [ToolBg] = "#F6F8FC",
        [ToolBorder] = "#DCE3EF",
        [ToolAccent] = "#4F5BD5",
        [UserBubbleBg] = "#E8F1FE",
        [UserBubbleBorder] = "#CBDFFB",
        [FinalBg] = "#F1FBF6",
        [FinalBorder] = "#C7E9D9",
        [BannerInfoBg] = "#EAF4FB",
        [BannerInfoBorder] = "#C6E0F0",
        [BannerErrorBg] = "#FDECEA",
        [BannerErrorBorder] = "#F3C4BE",
        [BadgeCeoBg] = "#E3F7F0",
        [BadgeToolsBg] = "#EAEBFC",
        [BrandTileBg] = "#DDF3EA",
        [StepIndexBg] = "#EEF2F8",
        [ButtonBg] = "#FFFFFF",
        [ButtonBorder] = "#D7DEE8",
        [ButtonText] = "#1B2836"
    };

    private static void ApplyAccent(Dictionary<string, string> colors, AccentColor accent, bool isDark)
    {
        var hex = AccentHex(accent);

        colors[Accent] = isDark ? hex : DarkenForLightSurface(accent);
        colors[AccentOn] = isDark ? "#04140F" : "#FFFFFF";
        colors[AccentSoft] = isDark ? SoftDark(accent) : SoftLight(accent);
        colors[AccentSoftText] = isDark ? hex : DarkenForLightSurface(accent);
        colors[AccentBorder] = isDark ? BorderDark(accent) : BorderLight(accent);
        colors[Ok] = isDark ? hex : DarkenForLightSurface(accent);
    }

    private static string DarkenForLightSurface(AccentColor accent) => accent switch
    {
        AccentColor.Blue => "#1D5FD0",
        AccentColor.Purple => "#6D3FD4",
        AccentColor.Amber => "#B4690E",
        AccentColor.Rose => "#C62A48",
        _ => "#0F9D74"
    };

    private static string SoftDark(AccentColor accent) => accent switch
    {
        AccentColor.Blue => "#12243D",
        AccentColor.Purple => "#1F1A3A",
        AccentColor.Amber => "#33260F",
        AccentColor.Rose => "#35141F",
        _ => "#153A30"
    };

    private static string SoftLight(AccentColor accent) => accent switch
    {
        AccentColor.Blue => "#E4EEFE",
        AccentColor.Purple => "#EFE9FE",
        AccentColor.Amber => "#FDF1DC",
        AccentColor.Rose => "#FDE8ED",
        _ => "#E3F7F0"
    };

    private static string BorderDark(AccentColor accent) => accent switch
    {
        AccentColor.Blue => "#1D3A5C",
        AccentColor.Purple => "#332A5C",
        AccentColor.Amber => "#5C451C",
        AccentColor.Rose => "#5C2433",
        _ => "#1D4A44"
    };

    private static string BorderLight(AccentColor accent) => accent switch
    {
        AccentColor.Blue => "#C3D9FB",
        AccentColor.Purple => "#D8CDF8",
        AccentColor.Amber => "#F1DDB8",
        AccentColor.Rose => "#F6C9D4",
        _ => "#BFE7DA"
    };

    private static void ApplyFonts(Dictionary<string, string> colors, FontSizePreference size)
    {
        var scale = FontScale(size);

        // Stored alongside the colours so one dictionary drives the whole shell.
        colors[FontBase] = Format(13 * scale);
        colors[FontTitle] = Format(19 * scale);
        colors[FontSubtitle] = Format(11.5 * scale);
        colors[FontLabel] = Format(10 * scale);
        colors[FontTiny] = Format(10.5 * scale);
        colors[FontMono] = Format(11 * scale);
        colors[FontBody] = Format(12.5 * scale);
        colors[FontButton] = Format(12 * scale);
    }

    private static string Format(double value)
        => Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture);
}

/// <summary>The resolved token set for one theme/accent/size combination.</summary>
public sealed record ThemePalette(
    AppTheme Variant,
    bool IsDark,
    AccentColor AccentColor,
    FontSizePreference FontSize,
    IReadOnlyDictionary<string, string> Colors)
{
    /// <summary>Colour tokens only (the <c>#rrggbb</c> values).</summary>
    public IReadOnlyDictionary<string, string> ColorValues
        => Colors
            .Where(pair => UiTheme.ColorTokens.Contains(pair.Key, StringComparer.Ordinal))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    /// <summary>Font tokens parsed as doubles.</summary>
    public IReadOnlyDictionary<string, double> FontValues
        => Colors
            .Where(pair => UiTheme.FontTokens.Contains(pair.Key, StringComparer.Ordinal))
            .ToDictionary(
                pair => pair.Key,
                pair => double.Parse(pair.Value, CultureInfo.InvariantCulture),
                StringComparer.Ordinal);

    /// <summary>Every token as a CSS custom property, e.g. <c>--color-app-bg: #070A10;</c>.</summary>
    public string ToCss()
    {
        var builder = new System.Text.StringBuilder(":root {\n");

        foreach (var (key, value) in Colors.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var name = key.StartsWith("Font", StringComparison.Ordinal)
                ? $"--font-{Kebab(key[4..])}"
                : $"--color-{Kebab(key)}";

            var suffix = key.StartsWith("Font", StringComparison.Ordinal) ? "px" : string.Empty;
            builder.Append($"  {name}: {value}{suffix};\n");
        }

        builder.Append("}\n");
        return builder.ToString();
    }

    private static string Kebab(string name)
    {
        var builder = new System.Text.StringBuilder(name.Length + 4);

        for (var index = 0; index < name.Length; index++)
        {
            var character = name[index];

            if (char.IsUpper(character) && index > 0)
            {
                builder.Append('-');
            }

            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }
}
