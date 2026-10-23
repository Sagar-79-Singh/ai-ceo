using AiCeo.Models;
using AiCeo.Options;
using AiCeo.Services;
using Xunit;

namespace AiCeo.Tests;

public class LlmConnectionsTests
{
    private static Agent AgentFor(LlmProvider provider, string? baseUrl = null, string? apiKey = null, string? modelId = null) => new()
    {
        Name = "Test",
        Role = "Tester",
        SystemPrompt = "prompt",
        Provider = provider,
        BaseUrl = baseUrl,
        ApiKey = apiKey,
        ModelId = modelId ?? LlmProviderCatalog.DefaultModelFor(provider)
    };

    [Theory]
    [InlineData(LlmProvider.OmniRoute, "http://localhost:20128/v1")]
    [InlineData(LlmProvider.OpenAI, "https://api.openai.com/v1")]
    [InlineData(LlmProvider.Gemini, "https://generativelanguage.googleapis.com/v1beta/openai")]
    public void Applies_the_provider_default_base_url(LlmProvider provider, string expected)
    {
        Assert.Equal(expected, LlmConnections.ResolveBaseUrl(provider, null, new OmniRouteOptions()));
    }

    [Fact]
    public void Custom_requires_a_base_url()
    {
        var agent = AgentFor(LlmProvider.Custom);

        var exception = Assert.Throws<LlmException>(() => LlmConnections.Resolve(agent, new OmniRouteOptions()));
        Assert.Contains("base URL", exception.Message, StringComparison.OrdinalIgnoreCase);

        agent.BaseUrl = "http://localhost:11434/v1";
        var connection = LlmConnections.Resolve(agent, new OmniRouteOptions());
        Assert.Equal("http://localhost:11434/v1", connection.BaseUrl);
    }

    [Fact]
    public void An_agent_without_its_own_url_uses_the_configured_omniroute_endpoint()
    {
        var options = new OmniRouteOptions { Endpoint = "http://gateway.internal:9000/v1/" };

        var connection = LlmConnections.Resolve(AgentFor(LlmProvider.OmniRoute), options);

        Assert.Equal("http://gateway.internal:9000/v1", connection.BaseUrl);
    }

    [Fact]
    public void Stored_key_wins_over_the_environment()
    {
        var agent = AgentFor(LlmProvider.OpenAI, apiKey: "sk-agent");

        var (key, source) = LlmConnections.ResolveApiKey(LlmProvider.OpenAI, agent.ApiKey, new OmniRouteOptions());

        Assert.Equal("sk-agent", key);
        Assert.Equal(LlmConnections.KeySourceStored, source);
    }

    [Fact]
    public void Missing_key_for_a_hosted_provider_is_a_clear_error()
    {
        var previous = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);

        try
        {
            var exception = Assert.Throws<LlmException>(
                () => LlmConnections.Resolve(AgentFor(LlmProvider.OpenAI), new OmniRouteOptions()));

            Assert.Contains("OPENAI_API_KEY", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", previous);
        }
    }

    [Fact]
    public void Environment_key_is_used_when_the_agent_has_none()
    {
        var previous = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        Environment.SetEnvironmentVariable("GEMINI_API_KEY", "env-gemini");

        try
        {
            var connection = LlmConnections.Resolve(AgentFor(LlmProvider.Gemini), new OmniRouteOptions());

            Assert.Equal("env-gemini", connection.ApiKey);
            Assert.Equal("env:GEMINI_API_KEY", connection.ApiKeySource);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEMINI_API_KEY", previous);
        }
    }

    [Fact]
    public void OmniRoute_falls_back_to_the_local_key()
    {
        var previous = Environment.GetEnvironmentVariable("OMNIROUTE_KEY");
        Environment.SetEnvironmentVariable("OMNIROUTE_KEY", null);

        try
        {
            var (key, source) = LlmConnections.ResolveApiKey(LlmProvider.OmniRoute, null, new OmniRouteOptions());

            Assert.Equal(OmniRouteOptions.FallbackApiKey, key);
            Assert.Equal(LlmConnections.KeySourceFallback, source);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OMNIROUTE_KEY", previous);
        }
    }

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
