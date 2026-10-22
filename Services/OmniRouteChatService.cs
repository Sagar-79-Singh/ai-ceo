using System.ClientModel;
using System.Net.Http.Headers;
using AiCeo.Models;
using AiCeo.Options;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;

namespace AiCeo.Services;

public sealed record ChatRequest(string ModelId, string SystemPrompt, string UserPrompt);

public sealed record OmniRouteHealth(bool Online, string Message, string Endpoint, string ModelId);

/// <summary>
/// The single door to the LLM. Every call goes through OmniRoute
/// (<c>http://localhost:20128/v1</c>) using the official <c>OpenAI</c> .NET client,
/// exactly as required by docs/RULES.md.
/// </summary>
public sealed class OmniRouteChatService
{
    public const string HttpClientName = "omniroute";

    private readonly OmniRouteOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OmniRouteChatService> _logger;

    public OmniRouteChatService(
        IOptions<OmniRouteOptions> options,
        IHttpClientFactory httpClientFactory,
        ILogger<OmniRouteChatService> logger)
    {
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public string Endpoint => _options.ResolveEndpoint();

    public string DefaultModel => string.IsNullOrWhiteSpace(_options.ModelId)
        ? OmniRouteOptions.DefaultModel
        : _options.ModelId.Trim();

    /// <summary>
    /// Builds the OmniRoute-configured client (RULES.md rule 3). A fresh client is created per
    /// call so that changes to <c>OMNIROUTE_KEY</c> are picked up without a restart.
    /// </summary>
    private OpenAIClient CreateClient()
    {
        return new OpenAIClient(
            new ApiKeyCredential(_options.ResolveApiKey()),
            new OpenAIClientOptions { Endpoint = new Uri(_options.ResolveEndpoint()) });
    }

    /// <summary>Single-shot chat completion against OmniRoute. Returns plain text.</summary>
    public async Task<string> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        var modelId = string.IsNullOrWhiteSpace(request.ModelId) ? DefaultModel : request.ModelId.Trim();
        var chatClient = CreateClient().GetChatClient(modelId);

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(request.SystemPrompt),
            new UserChatMessage(request.UserPrompt)
        };

        var completionOptions = new ChatCompletionOptions();
        if (_options.MaxOutputTokens > 0)
        {
            completionOptions.MaxOutputTokenCount = _options.MaxOutputTokens;
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timeoutSeconds = _options.TimeoutSeconds > 0 ? _options.TimeoutSeconds : 300;
        timeoutSource.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            var result = await chatClient.CompleteChatAsync(messages, completionOptions, timeoutSource.Token);
            var completion = result.Value;
            var text = string.Concat(completion.Content.Select(part => part.Text)).Trim();

            if (string.IsNullOrWhiteSpace(text))
            {
                throw new OmniRouteException(
                    $"OmniRoute returned an empty completion for model '{modelId}'. "
                    + "Check that the model id exists in the gateway.");
            }

            return text;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OmniRouteException(
                $"OmniRoute did not answer within {timeoutSeconds}s (model '{modelId}', endpoint {Endpoint}). "
                + "The gateway may be busy or the model may be unavailable.");
        }
        catch (ClientResultException exception)
        {
            _logger.LogError(exception, "OmniRoute call failed for model {Model}", modelId);
            throw new OmniRouteException(
                $"OmniRoute request failed for model '{modelId}': {exception.Message} "
                + $"Verify the gateway is running at {Endpoint}.",
                exception);
        }
        catch (HttpRequestException exception)
        {
            _logger.LogError(exception, "OmniRoute is unreachable at {Endpoint}", Endpoint);
            throw new OmniRouteException(
                $"Could not reach OmniRoute at {Endpoint}. Start the local gateway and try again.",
                exception);
        }
    }

    /// <summary>Lightweight connectivity probe used by the sidebar status badge.</summary>
    public async Task<OmniRouteHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        var endpoint = Endpoint;
        try
        {
            using var http = _httpClientFactory.CreateClient(HttpClientName);
            http.Timeout = TimeSpan.FromSeconds(6);

            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint + "/models");
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", _options.ResolveApiKey());

            using var response = await http.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return new OmniRouteHealth(true, "OmniRoute reachable", endpoint, DefaultModel);
            }

            return new OmniRouteHealth(
                false,
                $"Gateway answered HTTP {(int)response.StatusCode}",
                endpoint,
                DefaultModel);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new OmniRouteHealth(false, "Gateway offline", endpoint, DefaultModel);
        }
    }
}
