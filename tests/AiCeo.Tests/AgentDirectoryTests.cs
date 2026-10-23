using AiCeo.Models;
using AiCeo.Options;
using AiCeo.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace AiCeo.Tests;

public class AgentDirectoryTests
{
    private static AgentDirectory Create(TestDatabase database)
        => new(database.Db, Options.Create(new OmniRouteOptions()));

    private static AgentFormModel Form(LlmProvider provider = LlmProvider.OmniRoute, string? apiKey = "sk-secret-1234567890") => new()
    {
        Name = "Scout",
        Role = "Researcher",
        SystemPrompt = "You research.",
        Provider = provider,
        ModelId = "gpt-4o-mini",
        BaseUrl = provider == LlmProvider.Custom ? "http://localhost:11434/v1" : null,
        ApiKey = apiKey
    };

    [Fact]
    public async Task Create_stores_every_provider_field_and_the_first_agent_becomes_ceo()
    {
        using var database = TestDatabase.Create();

        var created = await Create(database).CreateAsync(Form(LlmProvider.Gemini));

        Assert.True(created.IsManager);
        Assert.Equal(LlmProvider.Gemini, created.Provider);
        Assert.Equal("gpt-4o-mini", created.ModelId);
        Assert.Equal("sk-secret-1234567890", created.ApiKey);
    }

    [Fact]
    public async Task Views_never_expose_the_raw_key()
    {
        using var database = TestDatabase.Create();
        var directory = Create(database);

        await directory.CreateAsync(Form(LlmProvider.OpenAI));
        var view = Assert.Single(await directory.ListViewsAsync());

        Assert.Equal(LlmProvider.OpenAI, view.Provider);
        Assert.Equal("OpenAI", view.ProviderDisplayName);
        Assert.Equal("https://api.openai.com/v1", view.EffectiveBaseUrl);
        Assert.True(view.HasStoredApiKey);
        Assert.Equal("sk-s…7890", view.ApiKeyMask);
        Assert.DoesNotContain("sk-secret-1234567890", view.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Updating_with_a_blank_key_keeps_the_stored_secret()
    {
        using var database = TestDatabase.Create();
        var directory = Create(database);
        var created = await directory.CreateAsync(Form());

        var form = Form(apiKey: null);
        form.Id = created.Id;
        form.Role = "Senior Researcher";

        var updated = await directory.UpdateAsync(created.Id, form);

        Assert.NotNull(updated);
        Assert.Equal("Senior Researcher", updated!.Role);
        Assert.Equal("sk-secret-1234567890", updated.ApiKey);
    }

    [Fact]
    public async Task Updating_can_replace_or_clear_the_key()
    {
        using var database = TestDatabase.Create();
        var directory = Create(database);
        var created = await directory.CreateAsync(Form());

        var replace = Form(apiKey: "sk-new-key-abcdefgh");
        await directory.UpdateAsync(created.Id, replace);
        Assert.Equal("sk-new-key-abcdefgh", (await directory.FindAsync(created.Id))!.ApiKey);

        var clear = Form(apiKey: null);
        clear.ClearApiKey = true;
        await directory.UpdateAsync(created.Id, clear);
        Assert.Null((await directory.FindAsync(created.Id))!.ApiKey);
    }

    [Fact]
    public async Task Custom_provider_without_a_base_url_is_rejected()
    {
        using var database = TestDatabase.Create();

        var form = Form(LlmProvider.Custom);
        form.BaseUrl = null;

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => Create(database).CreateAsync(form));
        Assert.Contains("Base URL", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_model_id_falls_back_to_the_provider_default()
    {
        using var database = TestDatabase.Create();

        var form = Form(LlmProvider.Gemini);
        form.ModelId = "";

        var created = await Create(database).CreateAsync(form);

        Assert.Equal(LlmProviderCatalog.GeminiDefaultModel, created.ModelId);
    }

    [Fact]
    public async Task Promoting_an_agent_leaves_exactly_one_ceo()
    {
        using var database = TestDatabase.Create();
        var directory = Create(database);

        var first = await directory.CreateAsync(Form());
        var second = await directory.CreateAsync(Form(apiKey: null));

        await directory.MakeManagerAsync(second.Id);

        var views = await directory.ListViewsAsync();
        Assert.Single(views.Where(view => view.IsManager));
        Assert.Equal(second.Id, views.Single(view => view.IsManager).Id);
        Assert.NotEqual(first.Id, views.Single(view => view.IsManager).Id);
    }
}
