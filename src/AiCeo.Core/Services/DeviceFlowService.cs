using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AiCeo.Models;

namespace AiCeo.Services;

/// <summary>
/// v2.0.1 — the OAuth 2.0 device authorisation grant ("device flow"), for the two providers that
/// issue tokens to a desktop app instead of to a server.
///
/// A device flow has two halves:
/// <list type="number">
///   <item><see cref="StartAsync"/> asks the provider for a short code and the URL the user types it
///   into. Nothing secret is involved yet.</item>
///   <item><see cref="WaitForTokenAsync"/> polls the token endpoint until the user authorises, the
///   code expires, or they decline — the provider answers <c>authorization_pending</c> while it
///   waits, which is the normal path, not an error.</item>
/// </list>
///
/// The endpoints are properties so the test suite can point the whole thing at a stub handler; the
/// defaults are the real ones. No secret is compiled in: GitHub's device-flow client id is a public
/// identifier, which is exactly what this grant is designed for.
/// </summary>
public sealed class DeviceFlowService
{
    /// <summary>Where the code is typed for GitHub.</summary>
    public const string GitHubVerificationUri = "https://github.com/login/device";

    /// <summary>Where the code is typed for ChatGPT/Codex.</summary>
    public const string OpenAiVerificationUri = "https://auth.openai.com/codex/device";

    /// <summary>The grant type GitHub (and every RFC 8628 server) expects on the token endpoint.</summary>
    public const string DeviceGrantType = "urn:ietf:params:oauth:grant-type:device_code";

    private readonly HttpClient _http;

    public DeviceFlowService(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    /// <summary>Origin of GitHub's OAuth endpoints (no trailing slash).</summary>
    public string GitHubAuthBase { get; init; } = "https://github.com";

    /// <summary>Origin of the GitHub REST API, used only to read the signed-in login.</summary>
    public string GitHubApiBase { get; init; } = "https://api.github.com";

    /// <summary>Origin of OpenAI's auth endpoints (no trailing slash).</summary>
    public string OpenAiAuthBase { get; init; } = "https://auth.openai.com";

    /// <summary>Public device-flow client id for Copilot.</summary>
    public string GitHubClientId { get; init; } = AuthMethodCatalog.GitHubClientId;

    /// <summary>Public device-flow client id for Codex.</summary>
    public string OpenAiClientId { get; init; } = AuthMethodCatalog.OpenAiClientId;

    /// <summary>True for the methods this service can handle.</summary>
    public static bool Supports(AuthMethod method)
        => method is AuthMethod.GitHubCopilot or AuthMethod.OpenAICodex;

    /// <summary>
    /// Asks the provider for a device code. Throws <see cref="DeviceFlowException"/> with a message
    /// that can be shown to the user verbatim.
    /// </summary>
    public Task<DeviceCodeGrant> StartAsync(AuthMethod method, CancellationToken cancellationToken = default)
        => method switch
        {
            AuthMethod.GitHubCopilot => StartGitHubAsync(cancellationToken),
            AuthMethod.OpenAICodex => StartOpenAiAsync(cancellationToken),
            _ => throw new DeviceFlowException($"{AuthMethodCatalog.DisplayNameFor(method)} does not use a device flow.")
        };

    /// <summary>
    /// Polls until the user authorises (success), declines, the code expires, or
    /// <paramref name="cancellationToken"/> is cancelled. Never throws for a pending authorisation.
    /// </summary>
    public async Task<DeviceAuthResult> WaitForTokenAsync(
        DeviceCodeGrant grant,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(grant);

        var interval = Math.Max(1, grant.Interval);
        var deadline = DateTime.UtcNow.AddSeconds(grant.ExpiresIn <= 0 ? 900 : grant.ExpiresIn);
        var lastError = "The device code expired before it was authorised. Start the sign-in again.";

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(interval), cancellationToken).ConfigureAwait(false);
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return DeviceAuthResult.Error("The sign-in timed out.");
            }

            DeviceAuthResult result;

            try
            {
                result = grant.Method == AuthMethod.GitHubCopilot
                    ? await PollGitHubAsync(grant, cancellationToken).ConfigureAwait(false)
                    : await PollOpenAiAsync(grant, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (DeviceFlowException exception)
            {
                // A transport hiccup must not abandon a code the user is still typing.
                lastError = exception.Message;
                continue;
            }
            catch (HttpRequestException exception)
            {
                lastError = "Could not reach the sign-in server: " + exception.Message;
                continue;
            }

            if (result.Success)
            {
                return result;
            }

            if (result.Status == "slow_down")
            {
                interval += 5;
                continue;
            }

            if (result.Status == "pending")
            {
                continue;
            }

            return result;
        }

        return DeviceAuthResult.Expired(lastError);
    }

    /// <summary>One convenience call: start, hand the code over, then wait. Used by tests.</summary>
    public async Task<DeviceAuthResult> SignInAsync(
        AuthMethod method,
        Action<DeviceCodeGrant>? onCode = null,
        CancellationToken cancellationToken = default)
    {
        var grant = await StartAsync(method, cancellationToken).ConfigureAwait(false);
        onCode?.Invoke(grant);
        return await WaitForTokenAsync(grant, cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------------------ GitHub

    private async Task<DeviceCodeGrant> StartGitHubAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            GitHubAuthBase + "/login/device/code")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = GitHubClientId,
                ["scope"] = AuthMethodCatalog.GitHubScope
            })
        };

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("AiCeoOrchestrator/2.0.1");

        using var document = await SendJsonAsync(request, "GitHub", cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;

        var deviceCode = ReadString(root, "device_code");
        var userCode = ReadString(root, "user_code");

        if (string.IsNullOrWhiteSpace(deviceCode) || string.IsNullOrWhiteSpace(userCode))
        {
            throw new DeviceFlowException(
                "GitHub did not return a device code. " + ReadString(root, "error_description"));
        }

        return new DeviceCodeGrant(
            AuthMethod.GitHubCopilot,
            deviceCode,
            userCode,
            ReadString(root, "verification_uri") is { Length: > 0 } uri ? uri : GitHubVerificationUri,
            ReadInt(root, "expires_in", 900),
            ReadInt(root, "interval", 5));
    }

    private async Task<DeviceAuthResult> PollGitHubAsync(DeviceCodeGrant grant, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            GitHubAuthBase + "/login/oauth/access_token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = GitHubClientId,
                ["device_code"] = grant.DeviceCode,
                ["grant_type"] = DeviceGrantType
            })
        };

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("AiCeoOrchestrator/2.0.1");

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new DeviceFlowException($"GitHub answered with {response.StatusCode} instead of JSON.");
        }

        using (document)
        {
            var root = document.RootElement;
            var token = ReadString(root, "access_token");

            if (!string.IsNullOrWhiteSpace(token))
            {
                var login = await ReadGitHubLoginAsync(token, cancellationToken).ConfigureAwait(false);
                return DeviceAuthResult.Authorized(token, login);
            }

            return MapError(ReadString(root, "error"), ReadString(root, "error_description"));
        }
    }

    /// <summary>The signed-in login, for the "Signed in as …" line. Best effort: a token without the
    /// <c>read:user</c> scope has no login to report and that must not fail the sign-in.</summary>
    private async Task<string?> ReadGitHubLoginAsync(string token, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, GitHubApiBase + "/user");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.UserAgent.ParseAdd("AiCeoOrchestrator/2.0.1");

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);
            var login = ReadString(document.RootElement, "login");

            return string.IsNullOrWhiteSpace(login) ? null : login;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------------------- OpenAI

    private async Task<DeviceCodeGrant> StartOpenAiAsync(CancellationToken cancellationToken)
    {
        using var request = JsonRequest(
            OpenAiAuthBase + "/api/accounts/deviceauth/usercode",
            new Dictionary<string, object?> { ["client_id"] = OpenAiClientId });

        using var document = await SendJsonAsync(request, "ChatGPT", cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;

        var deviceAuthId = ReadString(root, "device_auth_id");
        var userCode = ReadString(root, "user_code");

        if (string.IsNullOrWhiteSpace(deviceAuthId) || string.IsNullOrWhiteSpace(userCode))
        {
            throw new DeviceFlowException(
                "ChatGPT did not return a device code. " + ReadString(root, "message"));
        }

        return new DeviceCodeGrant(
            AuthMethod.OpenAICodex,
            deviceAuthId,
            userCode,
            OpenAiVerificationUri,
            ReadInt(root, "expires_in", 900),
            ReadInt(root, "interval", 5),
            Extra: deviceAuthId);
    }

    private async Task<DeviceAuthResult> PollOpenAiAsync(DeviceCodeGrant grant, CancellationToken cancellationToken)
    {
        using var poll = JsonRequest(
            OpenAiAuthBase + "/api/accounts/deviceauth/token",
            new Dictionary<string, object?>
            {
                ["device_auth_id"] = grant.Extra ?? grant.DeviceCode,
                ["user_code"] = grant.UserCode
            });

        using var response = await _http.SendAsync(poll, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        // While the user is still typing, these endpoints answer 403/404 (and sometimes 400 with no
        // body). That is the pending path, not a failure.
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound
            || (response.StatusCode is HttpStatusCode.BadRequest && string.IsNullOrWhiteSpace(body)))
        {
            return DeviceAuthResult.Pending();
        }

        if (!response.IsSuccessStatusCode)
        {
            return DeviceAuthResult.Error(
                $"ChatGPT answered {(int)response.StatusCode} while waiting for the code to be authorised.");
        }

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new DeviceFlowException("ChatGPT answered with something that is not JSON.");
        }

        using (document)
        {
            var root = document.RootElement;
            var code = ReadString(root, "authorization_code");

            if (string.IsNullOrWhiteSpace(code))
            {
                // Some deployments return the token straight from the poll.
                var direct = ReadString(root, "access_token");
                if (!string.IsNullOrWhiteSpace(direct))
                {
                    return DeviceAuthResult.Authorized(direct, LoginFromJwt(direct));
                }

                return MapError(ReadString(root, "error"), ReadString(root, "message"));
            }

            return await ExchangeOpenAiCodeAsync(code, ReadString(root, "code_verifier"), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task<DeviceAuthResult> ExchangeOpenAiCodeAsync(
        string authorizationCode,
        string codeVerifier,
        CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = authorizationCode,
            ["redirect_uri"] = OpenAiAuthBase + "/deviceauth/callback",
            ["client_id"] = OpenAiClientId
        };

        if (!string.IsNullOrWhiteSpace(codeVerifier))
        {
            payload["code_verifier"] = codeVerifier;
        }

        using var request = JsonRequest(OpenAiAuthBase + "/oauth/token", payload);
        using var document = await SendJsonAsync(request, "ChatGPT", cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;

        var token = ReadString(root, "access_token");
        if (string.IsNullOrWhiteSpace(token))
        {
            return MapError(ReadString(root, "error"), ReadString(root, "error_description"));
        }

        var login = LoginFromJwt(ReadString(root, "id_token")) ?? LoginFromJwt(token);
        return DeviceAuthResult.Authorized(token, login);
    }

    /// <summary>
    /// Reads the e-mail claim out of a JWT so the UI can say who is signed in. An id_token is not a
    /// secret and is not verified here — this is a label, never an authorisation decision.
    /// </summary>
    internal static string? LoginFromJwt(string? jwt)
    {
        if (string.IsNullOrWhiteSpace(jwt))
        {
            return null;
        }

        var parts = jwt.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');

            var bytes = Convert.FromBase64String(payload);
            using var document = JsonDocument.Parse(bytes);

            foreach (var claim in new[] { "email", "preferred_username", "name" })
            {
                var value = ReadString(document.RootElement, claim);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return null;
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------------------ plumbing

    /// <summary>Maps an OAuth error code onto a result the UI can print.</summary>
    private static DeviceAuthResult MapError(string error, string? description)
    {
        var message = string.IsNullOrWhiteSpace(description) ? error : description;

        return error switch
        {
            "authorization_pending" => DeviceAuthResult.Pending(),
            "slow_down" => DeviceAuthResult.SlowDown(),
            "expired_token" or "code_expired" => DeviceAuthResult.Expired(
                "The code expired before it was authorised. Start the sign-in again."),
            "access_denied" or "authorization_denied" => DeviceAuthResult.Denied(
                "The sign-in was declined in the browser."),
            "device_code_expired" => DeviceAuthResult.Expired(
                "The code expired before it was authorised. Start the sign-in again."),
            _ => DeviceAuthResult.Error(
                string.IsNullOrWhiteSpace(message) ? "The sign-in server refused the request." : message)
        };
    }

    private static HttpRequestMessage JsonRequest(string url, Dictionary<string, object?> payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("AiCeoOrchestrator/2.0.1");

        return request;
    }

    private async Task<JsonDocument> SendJsonAsync(
        HttpRequestMessage request,
        string provider,
        CancellationToken cancellationToken)
    {
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new DeviceFlowException(
                $"{provider} refused the sign-in request ({(int)response.StatusCode}). {Trim(body, 200)}".Trim());
        }

        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new DeviceFlowException($"{provider} answered with something that is not JSON.");
        }
    }

    private static string ReadString(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object
           && root.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int ReadInt(JsonElement root, string name, int fallback)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var value))
        {
            return fallback;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
            _ => fallback
        };
    }

    private static string Trim(string value, int max)
        => string.IsNullOrEmpty(value) ? string.Empty : value.Length <= max ? value : value[..max] + "…";
}

/// <summary>A device-flow failure with a message that is safe to show the user.</summary>
public sealed class DeviceFlowException : Exception
{
    public DeviceFlowException(string message) : base(message)
    {
    }

    public DeviceFlowException(string message, Exception inner) : base(message, inner)
    {
    }
}
