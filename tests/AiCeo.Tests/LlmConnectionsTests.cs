using AiCeo.Models;
using AiCeo.Options;
using AiCeo.Services;
using Xunit;

namespace AiCeo.Tests;

/// <summary>
/// v1.0.16: nothing is hardcoded any more. Every value either comes from the connection the user
/// saved in the first-run setup, from the agent's own override, or from an environment variable —
/// and when none of them is present the resolver says so instead of inventing a key or a model.
/// </summary>
public class LlmConnectionsTests
{
    private static readonly LlmSetup Configured = new(
        LlmProvider.OmniRoute,
        null,
        "sk-settings-key-0001",
        "settings-model");

    private static Agent AgentFor(
        LlmProvider provider,
        string? baseUrl = null,
        string? apiKey = null,
        string? modelId = null) => new()
    {
        Name = "Test",
        Role = "Tester",
        SystemPrompt = "prompt",
        Provider = provider,
        BaseUrl = baseUrl,
        ApiKey = apiKey,
        ModelId = modelId ?? string.Empty
    };

    // ------------------------------------------------------------------ LlmSetup itself

    [Fact]
    public void An_empty_connection_is_not_configured()
    {
        Assert.False(LlmSetup.Unconfigured.IsConfigured);
        Assert.False(LlmSetup.Unconfigured.HasApiKey);
        Assert.False(LlmSetup.Unconfigured.HasModel);
        Assert.Contains("not configured", LlmSetup.Unconfigured.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_connection_needs_both_a_key_and_a_model()
    {
        Assert.False(new LlmSetup(LlmProvider.OmniRoute, null, "sk-key", null).IsConfigured);
        Assert.False(new LlmSetup(LlmProvider.OmniRoute, null, null, "model").IsConfigured);
        Assert.True(new LlmSetup(LlmProvider.OmniRoute, null, "sk-key", "model").IsConfigured);
    }

    [Fact]
    public void Validate_rejects_the_missing_pieces_with_readable_messages()
    {
        var noKey = Assert.Throws<ArgumentException>(
            () => new LlmSetup(LlmProvider.OpenAI, null, null, "gpt").Validate());
        Assert.Contains("API key", noKey.Message, StringComparison.OrdinalIgnoreCase);

        var noModel = Assert.Throws<ArgumentException>(
            () => new LlmSetup(LlmProvider.OpenAI, null, "sk-key", "  ").Validate());
        Assert.Contains("model", noModel.Message, StringComparison.OrdinalIgnoreCase);

        var noBaseUrl = Assert.Throws<ArgumentException>(
            () => new LlmSetup(LlmProvider.Custom, null, "sk-key", "llama3.1").Validate());
        Assert.Contains("base URL", noBaseUrl.Message, StringComparison.OrdinalIgnoreCase);

        var badUrl = Assert.Throws<ArgumentException>(
            () => new LlmSetup(LlmProvider.Custom, "ftp://example.com/v1", "sk-key", "llama3.1").Validate());
        Assert.Contains("http", badUrl.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_trims_and_normalises_what_it_accepts()
    {
        var setup = new LlmSetup(LlmProvider.Custom, "  http://localhost:11434/v1/  ", "  sk-key  ", "  llama3.1  ")
            .Validate();

        Assert.Equal("http://localhost:11434/v1", setup.BaseUrl);
        Assert.Equal("sk-key", setup.ApiKey);
        Assert.Equal("llama3.1", setup.ModelId);
        Assert.Equal("http://localhost:11434/v1", setup.EffectiveBaseUrl);
    }

    [Fact]
    public void Every_provider_now_requires_a_key()
    {
        Assert.All(LlmProviderCatalog.All, info => Assert.True(info.RequiresApiKey));
    }

    // ------------------------------------------------------------------ base URL

    [Theory]
    [InlineData(LlmProvider.OmniRoute, "http://localhost:20128/v1")]
    [InlineData(LlmProvider.OpenAI, "https://api.openai.com/v1")]
    [InlineData(LlmProvider.Gemini, "https://generativelanguage.googleapis.com/v1beta/openai")]
    public void Applies_the_provider_default_base_url(LlmProvider provider, string expected)
    {
        Assert.Equal(
            expected,
            LlmConnections.ResolveBaseUrl(provider, null, LlmSetup.Unconfigured, new OmniRouteOptions()));
    }

    [Fact]
    public void The_configured_base_url_wins_over_the_provider_default()
    {
        var setup = new LlmSetup(LlmProvider.OpenAI, "https://proxy.internal/v1/", "sk-key", "gpt");

        Assert.Equal(
            "https://proxy.internal/v1",
            LlmConnections.ResolveBaseUrl(LlmProvider.OpenAI, null, setup, new OmniRouteOptions()));
    }

    [Fact]
    public void An_agent_url_wins_over_everything()
    {
        var setup = new LlmSetup(LlmProvider.OmniRoute, "http://settings:9000/v1", "sk-key", "model");
        var options = new OmniRouteOptions { Endpoint = "http://config:8080/v1" };

        Assert.Equal(
            "http://agent:7000/v1",
            LlmConnections.ResolveBaseUrl(LlmProvider.OmniRoute, "http://agent:7000/v1", setup, options));
    }

    [Fact]
    public void An_agent_without_its_own_url_uses_the_configured_omniroute_endpoint()
    {
        var options = new OmniRouteOptions { Endpoint = "http://gateway.internal:9000/v1/" };

        Assert.Equal(
            "http://gateway.internal:9000/v1",
            LlmConnections.ResolveBaseUrl(LlmProvider.OmniRoute, null, LlmSetup.Unconfigured, options));
    }

    [Fact]
    public void Custom_requires_a_base_url()
    {
        var agent = AgentFor(LlmProvider.Custom, apiKey: "sk-custom", modelId: "llama3.1");

        var exception = Assert.Throws<LlmException>(
            () => LlmConnections.Resolve(agent, LlmSetup.Unconfigured, new OmniRouteOptions()));
        Assert.Contains("base URL", exception.Message, StringComparison.OrdinalIgnoreCase);

        agent.BaseUrl = "http://localhost:11434/v1";
        var connection = LlmConnections.Resolve(agent, LlmSetup.Unconfigured, new OmniRouteOptions());
        Assert.Equal("http://localhost:11434/v1", connection.BaseUrl);
    }

    // ------------------------------------------------------------------ keys

    [Fact]
    public void An_agents_own_key_wins_over_the_settings()
    {
        var agent = AgentFor(LlmProvider.OpenAI, apiKey: "sk-agent");

        var (key, source) = LlmConnections.ResolveApiKey(
            LlmProvider.OpenAI,
            agent.ApiKey,
            Configured,
            new OmniRouteOptions());

        Assert.Equal("sk-agent", key);
        Assert.Equal(LlmConnections.KeySourceStored, source);
    }

    [Fact]
    public void The_key_saved_in_the_setup_is_used_when_the_agent_has_none()
    {
        var (key, source) = LlmConnections.ResolveApiKey(
            LlmProvider.OmniRoute,
            null,
            Configured,
            new OmniRouteOptions());

        Assert.Equal("sk-settings-key-0001", key);
        Assert.Equal(LlmConnections.KeySourceSettings, source);
    }

    [Fact]
    public void Environment_key_is_used_when_nothing_is_configured()
    {
        var previous = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        Environment.SetEnvironmentVariable("GEMINI_API_KEY", "env-gemini");

        try
        {
            var connection = LlmConnections.Resolve(
                AgentFor(LlmProvider.Gemini, modelId: "gemini-2.5-pro"),
                LlmSetup.Unconfigured,
                new OmniRouteOptions());

            Assert.Equal("env-gemini", connection.ApiKey);
            Assert.Equal("env:GEMINI_API_KEY", connection.ApiKeySource);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEMINI_API_KEY", previous);
        }
    }

    [Fact]
    public void OmniRoute_can_still_be_keyed_from_configuration()
    {
        var previous = Environment.GetEnvironmentVariable("OMNIROUTE_KEY");
        Environment.SetEnvironmentVariable("OMNIROUTE_KEY", null);

        try
        {
            var options = new OmniRouteOptions { ApiKey = "config-key" };
            var (key, source) = LlmConnections.ResolveApiKey(LlmProvider.OmniRoute, null, LlmSetup.Unconfigured, options);

            Assert.Equal("config-key", key);
            Assert.Equal("config:OmniRoute:ApiKey", source);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OMNIROUTE_KEY", previous);
        }
    }

    [Fact]
    public void A_completely_empty_install_asks_for_the_setup_screen()
    {
        var previous = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);

        try
        {
            var exception = Assert.Throws<LlmException>(() => LlmConnections.Resolve(
                AgentFor(LlmProvider.OpenAI),
                LlmSetup.Unconfigured,
                new OmniRouteOptions()));

            Assert.Contains("setup", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", previous);
        }
    }

    [Fact]
    public void TryResolve_reports_the_error_instead_of_throwing()
    {
        // Custom without a base URL fails deterministically, whatever the environment holds.
        var resolved = LlmConnections.TryResolve(
            AgentFor(LlmProvider.Custom),
            LlmSetup.Unconfigured,
            new OmniRouteOptions(),
            out var connection,
            out var error);

        Assert.False(resolved);
        Assert.Null(connection);
        Assert.Contains("base URL", error!, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ models

    [Fact]
    public void A_configured_model_is_used_for_every_agent_that_has_none()
    {
        var connection = LlmConnections.Resolve(
            AgentFor(LlmProvider.OmniRoute),
            Configured,
            new OmniRouteOptions());

        Assert.Equal("settings-model", connection.ModelId);
    }

    [Fact]
    public void An_agent_with_its_own_key_keeps_its_own_model()
    {
        var agent = AgentFor(LlmProvider.OmniRoute, apiKey: "sk-agent", modelId: "agent-model");

        Assert.Equal("agent-model", LlmConnections.ResolveModel(agent, Configured, LlmProvider.OmniRoute));
    }

    [Fact]
    public void An_agent_model_is_ignored_while_the_agent_has_no_key()
    {
        var agent = AgentFor(LlmProvider.OmniRoute, modelId: "agent-model");

        Assert.Equal("settings-model", LlmConnections.ResolveModel(agent, Configured, LlmProvider.OmniRoute));
    }

    [Fact]
    public void A_missing_model_is_an_error_not_a_default()
    {
        var exception = Assert.Throws<LlmException>(() => LlmConnections.Resolve(
            AgentFor(LlmProvider.OmniRoute, apiKey: "sk-key"),
            LlmSetup.Unconfigured,
            new OmniRouteOptions()));

        Assert.Contains("model", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(string.Empty, LlmProviderCatalog.DefaultBaseUrlFor(LlmProvider.Custom));
    }

    // ------------------------------------------------------------------ provider

    [Fact]
    public void The_configured_provider_applies_when_the_agent_has_no_key()
    {
        var setup = new LlmSetup(LlmProvider.Gemini, null, "sk-gemini", "gemini-2.5-pro");

        Assert.Equal(LlmProvider.Gemini, LlmConnections.ResolveProvider(AgentFor(LlmProvider.OmniRoute), setup));
        Assert.Equal(
            LlmProvider.OmniRoute,
            LlmConnections.ResolveProvider(AgentFor(LlmProvider.OmniRoute, apiKey: "sk-agent"), setup));
    }

    // ------------------------------------------------------------------ masking / validation

    [Theory]
    [InlineData("sk-1234567890abcdef", "sk-1…cdef")]
    [InlineData("short", "••••")]
    [InlineData(null, null)]
    public void Masks_keys_for_the_ui(string? key, string? expected)
    {
        Assert.Equal(expected, LlmConnections.MaskKey(key));
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://example.com/v1")]
    public void Rejects_non_http_urls(string url)
    {
        Assert.Throws<ArgumentException>(() => LlmConnections.NormalizeOptionalBaseUrl(LlmProvider.Custom, url));
    }

    [Fact]
    public void Trims_trailing_slashes()
    {
        Assert.Equal(
            "https://api.example.com/v1",
            LlmConnections.NormalizeOptionalBaseUrl(LlmProvider.Custom, "https://api.example.com/v1/"));
    }

    [Fact]
    public void Every_catalog_entry_is_complete()
    {
        foreach (var info in LlmProviderCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(info.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(info.ApiKeyEnvironmentVariable));
            Assert.False(string.IsNullOrWhiteSpace(info.Hint));
        }

        Assert.Equal(
            Enum.GetValues<LlmProvider>().OrderBy(value => value),
            LlmProviderCatalog.All.Select(info => info.Provider).OrderBy(value => value));
    }
}
