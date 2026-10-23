using System.ClientModel;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiCeo.Models;
using AiCeo.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;

namespace AiCeo.Services;

/// <summary>
/// Talks to any OpenAI-compatible backend (OmniRoute, OpenAI, Gemini, custom) through the
/// official <c>OpenAI</c> .NET client, one endpoint/key pair per connection, with function
/// calling for the workspace tools.
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

    public async Task<LlmReply> CompleteAsync(LlmCall call, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(call.ModelId))
        {
            throw new LlmException(call.Provider, call.BaseUrl, "(none)", "A model id is required.");
        }

        var chatClient = GetClient(call).GetChatClient(call.ModelId);
        var completionOptions = BuildOptions(call);

        var timeoutSeconds = _options.TimeoutSeconds > 0 ? _options.TimeoutSeconds : 300;
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            var result = await chatClient.CompleteChatAsync(
                ToChatMessages(call.Messages),
                completionOptions,
                timeoutSource.Token);

            var completion = result.Value;
            var text = string.Concat(completion.Content.Select(part => part.Text));
            var toolCalls = completion.ToolCalls
                .Select(toolCall => new LlmToolCallRequest(
                    toolCall.Id,
                    toolCall.FunctionName,
                    toolCall.FunctionArguments?.ToString() ?? "{}"))
                .ToList();

            if (string.IsNullOrWhiteSpace(text) && toolCalls.Count == 0)
            {
                throw new LlmException(
                    call.Provider,
                    call.BaseUrl,
                    call.ModelId,
                    "the provider returned an empty completion. Check the model id exists on that provider.");
            }

            return new LlmReply(
                string.IsNullOrWhiteSpace(text) ? null : text.Trim(),
                toolCalls,
                completion.FinishReason?.ToString() ?? "Stop");
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

            throw new LlmException(call.Provider, call.BaseUrl, call.ModelId, exception.Message, exception);
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

    private ChatCompletionOptions BuildOptions(LlmCall call)
    {
        var options = new ChatCompletionOptions();

        if (_options.MaxOutputTokens > 0)
        {
            options.MaxOutputTokenCount = _options.MaxOutputTokens;
        }

        foreach (var tool in call.Tools)
        {
            options.Tools.Add(ChatTool.CreateFunctionTool(
                tool.Name,
                tool.Description,
                BinaryData.FromString(tool.ParametersJson)));
        }

        if (call.Tools.Count > 0)
        {
            options.ToolChoice = call.ToolChoice switch
            {
                "required" => ChatToolChoice.CreateRequiredChoice(),
                "none" => ChatToolChoice.CreateNoneChoice(),
                null => ChatToolChoice.CreateAutoChoice(),
                var specific => ChatToolChoice.CreateFunctionChoice(specific)
            };
        }

        return options;
    }

    private static IReadOnlyList<ChatMessage> ToChatMessages(IReadOnlyList<LlmMessage> messages)
    {
        var result = new List<ChatMessage>(messages.Count);

        foreach (var message in messages)
        {
            switch (message)
            {
                case LlmSystemMessage system:
                    result.Add(new SystemChatMessage(system.Text));
                    break;

                case LlmUserMessage user:
                    result.Add(new UserChatMessage(user.Text));
                    break;

                case LlmAssistantMessage assistant:
                    result.Add(ToAssistantMessage(assistant));
                    break;

                case LlmToolMessage tool:
                    result.Add(new ToolChatMessage(tool.ToolCallId, tool.Content));
                    break;
            }
        }

        return result;
    }

    private static AssistantChatMessage ToAssistantMessage(LlmAssistantMessage message)
    {
        if (message.ToolCalls.Count == 0)
        {
            return new AssistantChatMessage(message.Text ?? string.Empty);
        }

        var calls = message.ToolCalls
            .Select(call => ChatToolCall.CreateFunctionToolCall(
                call.Id,
                call.Name,
                BinaryData.FromString(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson)))
            .ToList();

        var assistant = new AssistantChatMessage(calls);

        // Some providers reject an assistant turn that mixes text with tool calls, so text is only
        // attached when it is actually present.
        if (!string.IsNullOrWhiteSpace(message.Text))
        {
            assistant.Content.Add(ChatMessageContentPart.CreateTextPart(message.Text!));
        }

        return assistant;
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
