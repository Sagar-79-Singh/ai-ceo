using AiCeo.Models;
using AiCeo.Services;
using AiCeo.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AiCeo.Tests;

/// <summary>The UI preferences: storage, defaults, and the theme tokens they produce.</summary>
public class UserSettingsTests
{
    private static UserSettingsService Create(TestDatabase database) => new(database.Factory);

    [Fact]
    public async Task A_fresh_database_reports_the_defaults_and_creates_the_row()
    {
        using var database = TestDatabase.Create();
        var service = Create(database);

        var settings = await service.GetAsync();

        Assert.Equal(AppTheme.System, settings.Theme);
        Assert.Equal(AccentColor.Green, settings.AccentColor);
        Assert.Equal(FontSizePreference.Medium, settings.FontSize);

        using var db = database.NewContext();
        Assert.Equal(1, await db.UserSettings.CountAsync());
    }

    [Fact]
    public async Task Saving_round_trips_every_preference()
    {
        using var database = TestDatabase.Create();
        var service = Create(database);

        await service.SaveAsync(new UserSettingsSnapshot(AppTheme.Light, AccentColor.Purple, FontSizePreference.Large));

        // A second instance (as on the next launch) must see the same values.
        var reloaded = await Create(database).GetAsync();

        Assert.Equal(AppTheme.Light, reloaded.Theme);
        Assert.Equal(AccentColor.Purple, reloaded.AccentColor);
        Assert.Equal(FontSizePreference.Large, reloaded.FontSize);
    }

    [Fact]
    public async Task Preferences_are_stored_as_readable_text()
    {
        using var database = TestDatabase.Create();
        await Create(database).SaveAsync(new UserSettingsSnapshot(AppTheme.Dark, AccentColor.Amber, FontSizePreference.Small));

        // The test contexts share one open connection, so raw SQL sees exactly what EF wrote.
        using var db = database.NewContext();
        var connection = db.Database.GetDbConnection();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Theme, AccentColor, FontSize FROM UserSettings LIMIT 1;";

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("Dark", reader.GetString(0));
        Assert.Equal("Amber", reader.GetString(1));
        Assert.Equal("Small", reader.GetString(2));
    }

    [Fact]
    public async Task Save_notifies_listeners_and_reset_restores_the_defaults()
    {
        using var database = TestDatabase.Create();
        var service = Create(database);

        UserSettingsSnapshot? observed = null;
        service.Changed += (_, snapshot) => observed = snapshot;

        await service.SaveAsync(new UserSettingsSnapshot(AppTheme.Dark, AccentColor.Blue, FontSizePreference.Large));
        Assert.NotNull(observed);
        Assert.Equal(AccentColor.Blue, observed!.AccentColor);

        var reset = await service.ResetAsync();
        Assert.Equal(UserSettingsSnapshot.Default, reset);
        Assert.Equal(AccentColor.Green, (await service.GetAsync()).AccentColor);
    }

    [Fact]
    public async Task The_saved_connection_survives_an_appearance_change_and_a_reset()
    {
        using var database = TestDatabase.Create();
        var service = Create(database);

        database.SeedConnection(LlmProvider.Gemini, baseUrl: "https://proxy.internal/v1", modelId: "gemini-2.5-pro");

        var afterAppearance = await service.SaveAsync(
            new UserSettingsSnapshot(AppTheme.Light, AccentColor.Rose, FontSizePreference.Small));

        Assert.True(afterAppearance.IsConnectionConfigured);
        Assert.Equal(LlmProvider.Gemini, afterAppearance.Setup.Provider);
        Assert.Equal("gemini-2.5-pro", afterAppearance.Setup.ModelId);

        // "Reset appearance" must never wipe the user's key.
        var afterReset = await service.ResetAsync();
        Assert.Equal(AppTheme.System, afterReset.Theme);
        Assert.Equal(AccentColor.Green, afterReset.AccentColor);
        Assert.Equal(FontSizePreference.Medium, afterReset.FontSize);
        Assert.True(afterReset.IsConnectionConfigured);
        Assert.Equal("https://proxy.internal/v1", afterReset.Setup.BaseUrl);
    }

    [Fact]
    public async Task Unknown_enum_values_are_clamped_instead_of_throwing()
    {
        using var database = TestDatabase.Create();

        // Simulate a hand-edited / future-version row.
        using (var db = database.NewContext())
        {
            db.UserSettings.Add(new UserSettings
            {
                Theme = (AppTheme)99,
                AccentColor = (AccentColor)99,
                FontSize = (FontSizePreference)99
            });
            await db.SaveChangesAsync();
        }

        var settings = await Create(database).GetAsync();

        Assert.Equal(AppTheme.System, settings.Theme);
        Assert.Equal(AccentColor.Green, settings.AccentColor);
        Assert.Equal(FontSizePreference.Medium, settings.FontSize);
    }

    [Theory]
    [InlineData(AppTheme.Dark, true, AppTheme.Dark)]
    [InlineData(AppTheme.Dark, false, AppTheme.Dark)]
    [InlineData(AppTheme.Light, true, AppTheme.Light)]
    [InlineData(AppTheme.Light, false, AppTheme.Light)]
    [InlineData(AppTheme.System, true, AppTheme.Dark)]
    [InlineData(AppTheme.System, false, AppTheme.Light)]
    public void System_theme_follows_the_os_preference(AppTheme requested, bool osPrefersDark, AppTheme expected)
        => Assert.Equal(expected, UiTheme.ResolveVariant(requested, osPrefersDark));

    [Fact]
    public void Every_theme_token_has_a_value_in_both_variants()
    {
        foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
        {
            var palette = UiTheme.Build(UserSettingsSnapshot.Default with { Theme = theme }, systemPrefersDark: true);

            foreach (var token in UiTheme.ColorTokens)
            {
                Assert.True(palette.Colors.ContainsKey(token), $"{theme}: missing colour token {token}");
                Assert.StartsWith("#", palette.Colors[token]);
            }

            foreach (var token in UiTheme.FontTokens)
            {
                Assert.True(palette.Colors.ContainsKey(token), $"{theme}: missing font token {token}");
                Assert.True(double.Parse(palette.Colors[token]) > 0);
            }
        }
    }

    [Fact]
    public void Dark_and_light_are_actually_different()
    {
        var dark = UiTheme.Build(UserSettingsSnapshot.Default, systemPrefersDark: true);
        var light = UiTheme.Build(UserSettingsSnapshot.Default, systemPrefersDark: false);

        Assert.True(dark.IsDark);
        Assert.False(light.IsDark);
        Assert.NotEqual(dark.Colors[UiTheme.AppBg], light.Colors[UiTheme.AppBg]);
        Assert.NotEqual(dark.Colors[UiTheme.TextPrimary], light.Colors[UiTheme.TextPrimary]);
    }

    [Theory]
    [InlineData(AccentColor.Blue, "#3B82F6")]
    [InlineData(AccentColor.Purple, "#8B5CF6")]
    [InlineData(AccentColor.Amber, "#F59E0B")]
    [InlineData(AccentColor.Rose, "#F43F5E")]
    [InlineData(AccentColor.Green, "#5EE0B0")]
    public void The_accent_reaches_the_accent_tokens(AccentColor accent, string expected)
    {
        var palette = UiTheme.Build(
            new UserSettingsSnapshot(AppTheme.Dark, accent, FontSizePreference.Medium),
            systemPrefersDark: true);

        Assert.Equal(expected, palette.Colors[UiTheme.Accent]);
        Assert.Equal(expected, palette.Colors[UiTheme.AccentSoftText]);
    }

    [Fact]
    public void Font_size_preference_scales_every_font_token()
    {
        double Base(FontSizePreference size)
            => UiTheme.Build(
                new UserSettingsSnapshot(AppTheme.Dark, AccentColor.Green, size),
                systemPrefersDark: true).FontValues[UiTheme.FontBase];

        Assert.True(Base(FontSizePreference.Small) < Base(FontSizePreference.Medium));
        Assert.True(Base(FontSizePreference.Medium) < Base(FontSizePreference.Large));
        Assert.Equal(13, Base(FontSizePreference.Medium), 3);
    }

    [Fact]
    public void The_palette_can_be_exported_as_css_variables()
    {
        var css = UiTheme.Build(UserSettingsSnapshot.Default, systemPrefersDark: true).ToCss();

        Assert.Contains("--color-app-bg:", css, StringComparison.Ordinal);
        Assert.Contains("--color-text-primary:", css, StringComparison.Ordinal);
        Assert.Contains("--color-accent:", css, StringComparison.Ordinal);
        Assert.Contains("--font-base:", css, StringComparison.Ordinal);
        Assert.StartsWith(":root {", css, StringComparison.Ordinal);
        Assert.EndsWith("}\n", css, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_service_exposes_the_palette_for_the_saved_settings()
    {
        using var database = TestDatabase.Create();
        var service = Create(database);

        await service.SaveAsync(new UserSettingsSnapshot(AppTheme.Light, AccentColor.Rose, FontSizePreference.Small));

        var palette = await service.GetPaletteAsync(systemPrefersDark: true);

        Assert.False(palette.IsDark);
        Assert.Equal(AccentColor.Rose, palette.AccentColor);
        Assert.Equal(FontSizePreference.Small, palette.FontSize);
    }
}

/// <summary>Keeps the checked-in CSS mirror honest (it is generated from the same palette).</summary>
public class ThemeCssTests
{
    [Fact]
    public void The_checked_in_theme_css_matches_the_dark_and_light_palettes()
    {
        var path = FindThemeCss();
        Assert.True(path is not null, "src/AiCeo.Desktop/Assets/theme.css could not be located");

        var css = File.ReadAllText(path!);
        Assert.Contains(":root {", css, StringComparison.Ordinal);

        foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
        {
            var paletteCss = UiTheme.Build(
                UserSettingsSnapshot.Default with { Theme = theme },
                systemPrefersDark: false).ToCss();

            foreach (var line in paletteCss.Split('\n'))
            {
                if (!line.TrimStart().StartsWith("--", StringComparison.Ordinal))
                {
                    continue;
                }

                Assert.True(css.Contains(line, StringComparison.Ordinal), $"theme.css is missing: {line.Trim()}");
            }
        }

        Assert.Contains("[data-theme=\"light\"]", css, StringComparison.Ordinal);
    }

    [Fact]
    public void The_theme_css_declares_every_token()
    {
        var css = File.ReadAllText(FindThemeCss()!);

        foreach (var token in UiTheme.ColorTokens)
        {
            var name = "--color-" + string.Concat(token.Select((character, index) =>
                char.IsUpper(character) && index > 0
                    ? "-" + char.ToLowerInvariant(character)
                    : char.ToLowerInvariant(character).ToString()));

            Assert.Contains(name + ":", css, StringComparison.Ordinal);
        }

        foreach (var token in UiTheme.FontTokens)
        {
            var name = "--font-" + string.Concat(token[4..].Select((character, index) =>
                char.IsUpper(character) && index > 0
                    ? "-" + char.ToLowerInvariant(character)
                    : char.ToLowerInvariant(character).ToString()));

            Assert.Contains(name + ":", css, StringComparison.Ordinal);
        }
    }

    private static string? FindThemeCss()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        for (var depth = 0; depth < 10 && directory is not null; depth++)
        {
            var candidate = Path.Combine(directory.FullName, "src", "AiCeo.Desktop", "Assets", "theme.css");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
