using AiCeo.Data;
using AiCeo.Models;
using AiCeo.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AiCeo.Tests;

/// <summary>
/// The v1.0.16 first-run contract: a fresh install has no connection, saving one validates it,
/// persists exactly the four columns, publishes it without a restart, and never loses it.
/// </summary>
public class LlmSetupProviderTests
{
    [Fact]
    public async Task A_fresh_install_has_no_connection()
    {
        using var database = TestDatabase.Create();
        var provider = new LlmSetupProvider(database.Factory);

        var loaded = await provider.RefreshAsync();

        Assert.False(provider.IsConfigured);
        Assert.Equal(LlmSetup.Unconfigured, loaded);
        Assert.Null(loaded.ApiKey);
        Assert.Null(loaded.ModelId);
    }

    [Fact]
    public async Task Saving_stores_the_four_columns_and_publishes_them()
    {
        using var database = TestDatabase.Create();
        var provider = new LlmSetupProvider(database.Factory);

        var saved = await provider.SaveAsync(new LlmSetup(
            LlmProvider.OmniRoute,
            "http://localhost:20128/v1/",
            "  sk-first-run-key  ",
            "  my-model  "));

        Assert.True(saved.IsConfigured);
        Assert.True(provider.IsConfigured);
        Assert.Equal("sk-first-run-key", provider.Current.ApiKey);
        Assert.Equal("my-model", provider.Current.ModelId);
        Assert.Equal("http://localhost:20128/v1", provider.Current.BaseUrl);

        // A second instance (the next launch) reads the same row back.
        var reloaded = await new LlmSetupProvider(database.Factory).RefreshAsync();
        Assert.Equal(provider.Current, reloaded);

        using var db = database.NewContext();
        var row = await db.UserSettings.AsNoTracking().SingleAsync();
        Assert.Equal("sk-first-run-key", row.ApiKey);
        Assert.Equal("my-model", row.ModelId);
        Assert.Equal("http://localhost:20128/v1", row.BaseUrl);
        Assert.Equal(LlmProvider.OmniRoute, row.Provider);
        Assert.Equal(AppTheme.System, row.Theme); // appearance is untouched
    }

    [Theory]
    [InlineData(null, "model")]
    [InlineData("sk-key", null)]
    [InlineData("sk-key", "  ")]
    public async Task An_incomplete_connection_is_rejected_and_nothing_is_written(string? apiKey, string? modelId)
    {
        using var database = TestDatabase.Create();
        var provider = new LlmSetupProvider(database.Factory);

        await Assert.ThrowsAsync<ArgumentException>(
            () => provider.SaveAsync(new LlmSetup(LlmProvider.OpenAI, null, apiKey, modelId)));

        Assert.False(provider.IsConfigured);

        using var db = database.NewContext();
        Assert.Equal(0, await db.UserSettings.CountAsync());
    }

    [Fact]
    public async Task A_rejected_save_leaves_the_previous_connection_intact()
    {
        using var database = TestDatabase.Create();
        var provider = new LlmSetupProvider(database.Factory);

        await provider.SaveAsync(new LlmSetup(LlmProvider.OpenAI, null, "sk-good-key", "gpt-good"));

        await Assert.ThrowsAsync<ArgumentException>(
            () => provider.SaveAsync(new LlmSetup(LlmProvider.OpenAI, null, "sk-good-key", "")));

        Assert.Equal("gpt-good", provider.Current.ModelId);
        Assert.Equal("gpt-good", (await new LlmSetupProvider(database.Factory).RefreshAsync()).ModelId);
    }

    [Fact]
    public async Task Saving_again_replaces_the_connection_and_keeps_the_appearance()
    {
        using var database = TestDatabase.Create();

        using (var db = database.NewContext())
        {
            db.UserSettings.Add(new UserSettings { Theme = AppTheme.Light, AccentColor = AccentColor.Rose });
            await db.SaveChangesAsync();
        }

        var provider = new LlmSetupProvider(database.Factory);
        await provider.SaveAsync(new LlmSetup(LlmProvider.Gemini, null, "sk-gemini", "gemini-2.5-pro"));
        await provider.SaveAsync(new LlmSetup(LlmProvider.OpenAI, null, "sk-openai", "gpt-5.4-mini"));

        Assert.Equal(LlmProvider.OpenAI, provider.Current.Provider);
        Assert.Equal("gpt-5.4-mini", provider.Current.ModelId);

        using var check = database.NewContext();
        var row = await check.UserSettings.AsNoTracking().SingleAsync();
        Assert.Equal(AppTheme.Light, row.Theme);
        Assert.Equal(AccentColor.Rose, row.AccentColor);
        Assert.Equal("sk-openai", row.ApiKey);
    }

    [Fact]
    public async Task The_page_only_ever_sees_a_mask()
    {
        using var database = TestDatabase.Create();
        var provider = new LlmSetupProvider(database.Factory);

        var saved = await provider.SaveAsync(
            new LlmSetup(LlmProvider.OmniRoute, null, "sk-1234567890abcdef", "my-model"));

        Assert.Equal("sk-1…cdef", saved.MaskedApiKey);
        Assert.DoesNotContain("1234567890", saved.Describe(), StringComparison.Ordinal);
        Assert.Contains("my-model", saved.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Clear_forgets_the_in_memory_value_only()
    {
        using var database = TestDatabase.Create();
        var provider = new LlmSetupProvider(database.Factory);

        database.SeedConnection();
        provider.RefreshAsync().GetAwaiter().GetResult();
        Assert.True(provider.IsConfigured);

        provider.Clear();

        Assert.False(provider.IsConfigured);
    }
}
