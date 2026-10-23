using System.ClientModel;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using AiCeo.Models;
using AiCeo.Options;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;

namespace AiCeo.Services;

/// <summary>
/// Talks to any OpenAI-compatible backend (OmniRoute, OpenAI, Gemini, custom) through the
/// official <c>OpenAI</c> .NET client, one endpoint/key pair per connection.
/// </summary>
public sealed class LlmGateway : ILlmGateway
{
    public const string HttpClientName = "llm-probe";

    private readonly OmniRouteOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<LlmGateway> _logger;

    // OpenAI clients are immutable and thread-safe; reusing them avoids a TLS handshake per call.
    private readonly ConcurrentDictionary<string, OpenAIClient> _clients = new(StringComparer.Ordinal);

    public LlmGateway(
        IOptions<OmniRouteOptions> options,
        IHttpClientFactory httpClientFactory,
        ILogger<LlmGateway> logger)
    {
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<string> CompleteAsync(LlmCall call, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(call.ModelId))
        {
            throw new LlmException(call.Provider, call.BaseUrl, "(none)", "A model id is required.");
        }

        var chatClient = GetClient(call).GetChatClient(call.ModelId);

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(call.SystemPrompt),
            new UserChatMessage(call.UserPrompt)
        };

        var completionOptions = new ChatCompletionOptions();
        if (_options.MaxOutputTokens > 0)
        {
            completionOptions.MaxOutputTokenCount = _options.MaxOutputTokens;
        }

        var timeoutSeconds = _options.TimeoutSeconds > 0 ? _options.TimeoutSeconds : 300;
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            var result = await chatClient.CompleteChatAsync(messages, completionOptions, timeoutSource.Token);
            var text = string.Concat(result.Value.Content.Select(part => part.Text)).Trim();

            if (string.IsNullOrWhiteSpace(text))
            {
                throw new LlmException(
                    call.Provider,
                    call.BaseUrl,
                    call.ModelId,
                    "the provider returned an empty completion. Check the model id exists on that provider.");
            }

            return text;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LlmException(
                call.Provider,
                call.BaseUrl,
                call.ModelId,
                $"no answer within {timeoutSeconds}s. The provider may be busy or the model unavailable.");
        }
        catch (ClientResultException exception)
        {
            _logger.LogError(
                exception,
                "{Provider} call failed for {Agent} (model {Model})",
                call.Provider,
                call.AgentName,
                call.ModelId);

            throw new LlmException(
                call.Provider,
                call.BaseUrl,
                call.ModelId,
                exception.Message,
                exception);
        }
        catch (HttpRequestException exception)
        {
            _logger.LogError(exception, "{Provider} is unreachable at {Endpoint}", call.Provider, call.BaseUrl);

            throw new LlmException(
                call.Provider,
                call.BaseUrl,
                call.ModelId,
                "could not connect. Check the base URL and that the server is running.",
                exception);
        }
    }

    public async Task<ProviderHealth> CheckAsync(LlmCall probe, CancellationToken cancellationToken = default)
    {
        var providerName = LlmProviderCatalog.DisplayNameFor(probe.Provider);

        try
        {
            using var http = _httpClientFactory.CreateClient(HttpClientName);
            http.Timeout = TimeSpan.FromSeconds(6);

            using var request = new HttpRequestMessage(HttpMethod.Get, probe.BaseUrl.TrimEnd('/') + "/models");
            if (!string.IsNullOrWhiteSpace(probe.ApiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", probe.ApiKey);
            }

            using var response = await http.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return new ProviderHealth(true, $"{providerName} reachable", providerName, probe.BaseUrl, probe.ModelId);
            }

            var hint = (int)response.StatusCode is 401 or 403
                ? "API key rejected"
                : $"HTTP {(int)response.StatusCode}";

            return new ProviderHealth(false, $"{providerName}: {hint}", providerName, probe.BaseUrl, probe.ModelId);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogDebug(exception, "Health probe failed for {Provider} at {Endpoint}", probe.Provider, probe.BaseUrl);
            return new ProviderHealth(false, $"{providerName} unreachable", providerName, probe.BaseUrl, probe.ModelId);
        }
    }

    private OpenAIClient GetClient(LlmCall call)
    {
        var cacheKey = $"{call.BaseUrl}\n{Fingerprint(call.ApiKey)}";

        return _clients.GetOrAdd(cacheKey, _ => new OpenAIClient(
            new ApiKeyCredential(string.IsNullOrWhiteSpace(call.ApiKey) ? "not-required" : call.ApiKey),
            new OpenAIClientOptions { Endpoint = new Uri(call.BaseUrl) }));
    }

    private static string Fingerprint(string apiKey)
    {
        if (string.IsNullOrEmpty(apiKey))
        {
            return "-";
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));
        return Convert.ToHexString(hash, 0, 8);
    }
}
