using System.Net;
using System.Net.Http;
using System.Text;
using AiCeo.Models;
using AiCeo.Services;
using Xunit;

namespace AiCeo.Tests;

/// <summary>
/// v2.0.1 device-flow sign-in. The HTTP half is exercised against a scripted handler, so the test
/// proves the wire protocol (device code → poll → token) without touching github.com or openai.com.
/// </summary>
public class DeviceFlowTests
{
    [Fact]
    public async Task GitHub_issues_a_code_and_a_token()
    {
        var handler = new ScriptedHandler()
            .Json("/login/device/code", """
                {"device_code":"dev-123","user_code":"ABCD-1234","verification_uri":"https://github.com/login/device","expires_in":900,"interval":1}
                """)
            .Json("/login/oauth/access_token", """{"access_token":"gho_test_token","token_type":"bearer","scope":"read:user"}""")
            .Json("/user", """{"login":"octocat"}""");

        var service = new DeviceFlowService(new HttpClient(handler));

        var grant = await service.StartAsync(AuthMethod.GitHubCopilot);

        Assert.Equal("ABCD-1234", grant.UserCode);
        Assert.Equal("https://github.com/login/device", grant.VerificationUri);
        Assert.Equal(1, grant.Interval);

        var result = await service.WaitForTokenAsync(grant);

        Assert.True(result.Success);
        Assert.Equal("gho_test_token", result.AccessToken);
        Assert.Equal("octocat", result.Login);

        // The device-flow grant type and the public client id are what GitHub requires.
        Assert.Contains("login/device/code", handler.Requests[0].Url, StringComparison.Ordinal);
        Assert.Contains("client_id=" + AuthMethodCatalog.GitHubClientId, handler.Requests[0].Body, StringComparison.Ordinal);
        Assert.Contains("scope=read%3Auser", handler.Requests[0].Body, StringComparison.Ordinal);
        Assert.Contains("grant_type=" + Uri.EscapeDataString(DeviceFlowService.DeviceGrantType), handler.Requests[1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pending_authorisation_is_polled_until_the_user_approves()
    {
        var handler = new ScriptedHandler()
            .Json("/login/device/code", """
                {"device_code":"dev-1","user_code":"AAAA-0001","verification_uri":"https://github.com/login/device","expires_in":60,"interval":1}
                """)
            .Json("/login/oauth/access_token", """{"error":"authorization_pending","error_description":"pending"}""")
            .Json("/login/oauth/access_token", """{"error":"slow_down","error_description":"slow down"}""")
            .Json("/login/oauth/access_token", """{"access_token":"gho_late","token_type":"bearer"}""")
            .Json("/user", """{"login":"late-user"}""");

        var service = new DeviceFlowService(new HttpClient(handler));

        var result = await service.SignInAsync(AuthMethod.GitHubCopilot);

        Assert.True(result.Success);
        Assert.Equal("gho_late", result.AccessToken);
        Assert.Equal("late-user", result.Login);
    }

    [Fact]
    public async Task A_declined_authorisation_fails_with_the_reason()
    {
        var handler = new ScriptedHandler()
            .Json("/login/device/code", """
                {"device_code":"dev-2","user_code":"BBBB-0002","verification_uri":"https://github.com/login/device","expires_in":60,"interval":1}
                """)
            .Json("/login/oauth/access_token", """{"error":"access_denied","error_description":"The user denied the request"}""");

        var service = new DeviceFlowService(new HttpClient(handler));

        var result = await service.SignInAsync(AuthMethod.GitHubCopilot);

        Assert.False(result.Success);
        Assert.Equal("denied", result.Status);
        Assert.Contains("declined", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_expired_code_is_reported_as_expired()
    {
        var handler = new ScriptedHandler()
            .Json("/login/device/code", """
                {"device_code":"dev-3","user_code":"CCCC-0003","verification_uri":"https://github.com/login/device","expires_in":60,"interval":1}
                """)
            .Json("/login/oauth/access_token", """{"error":"expired_token","error_description":"expired"}""");

        var service = new DeviceFlowService(new HttpClient(handler));

        var result = await service.SignInAsync(AuthMethod.GitHubCopilot);

        Assert.False(result.Success);
        Assert.Equal("expired", result.Status);
    }

    [Fact]
    public async Task Codex_requests_a_code_then_exchanges_it_for_a_token()
    {
        var handler = new ScriptedHandler()
            .Json("/api/accounts/deviceauth/usercode", """
                {"device_auth_id":"dev-auth-1","user_code":"WXYZ-9876","interval":1,"expires_in":900}
                """)
            .Json("/api/accounts/deviceauth/token", """{"authorization_code":"ac-1","code_verifier":"verifier-1"}""")
            .Json("/oauth/token", """{"access_token":"codex_token","id_token":"header.eyJlbWFpbCI6Im1lQGV4YW1wbGUuY29tIn0.sig"}""");

        var service = new DeviceFlowService(new HttpClient(handler));

        var grant = await service.StartAsync(AuthMethod.OpenAICodex);

        Assert.Equal("WXYZ-9876", grant.UserCode);
        Assert.Equal(DeviceFlowService.OpenAiVerificationUri, grant.VerificationUri);
        Assert.Equal("dev-auth-1", grant.Extra);

        var result = await service.WaitForTokenAsync(grant);

        Assert.True(result.Success);
        Assert.Equal("codex_token", result.AccessToken);
        Assert.Equal("me@example.com", result.Login);

        Assert.Contains("/api/accounts/deviceauth/usercode", handler.Requests[0].Url, StringComparison.Ordinal);
        Assert.Contains("\"client_id\"", handler.Requests[0].Body, StringComparison.Ordinal);

        // The last request is the token exchange; it carries the PKCE verifier the poll handed back.
        var exchange = handler.Requests[^1];
        Assert.Contains("/oauth/token", exchange.Url, StringComparison.Ordinal);
        Assert.Contains("\"code_verifier\":\"verifier-1\"", exchange.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_still_pending_codex_poll_is_not_an_error()
    {
        var handler = new ScriptedHandler()
            .Json("/api/accounts/deviceauth/usercode", """{"device_auth_id":"dev-auth-2","user_code":"CODE-1","interval":1,"expires_in":60}""")
            .Status("/api/accounts/deviceauth/token", HttpStatusCode.Forbidden, string.Empty)
            .Json("/api/accounts/deviceauth/token", """{"authorization_code":"ac-2","code_verifier":"v-2"}""")
            .Json("/oauth/token", """{"access_token":"codex_token_2"}""");

        var service = new DeviceFlowService(new HttpClient(handler));

        var result = await service.SignInAsync(AuthMethod.OpenAICodex);

        Assert.True(result.Success);
        Assert.Equal("codex_token_2", result.AccessToken);
    }

    [Fact]
    public void Only_the_two_device_flow_methods_are_supported()
    {
        Assert.True(DeviceFlowService.Supports(AuthMethod.GitHubCopilot));
        Assert.True(DeviceFlowService.Supports(AuthMethod.OpenAICodex));
        Assert.False(DeviceFlowService.Supports(AuthMethod.ApiKey));
    }

    [Fact]
    public void A_jwt_login_is_read_from_the_email_claim()
    {
        // {"sub":"1","email":"pilot@example.com"}
        const string token = "h.eyJzdWIiOiIxIiwiZW1haWwiOiJwaWxvdEBleGFtcGxlLmNvbSJ9.s";

        Assert.Equal("pilot@example.com", DeviceFlowService.LoginFromJwt(token));
        Assert.Null(DeviceFlowService.LoginFromJwt("not-a-jwt"));
        Assert.Null(DeviceFlowService.LoginFromJwt(null));
    }

    [Fact]
    public async Task A_signed_in_token_becomes_the_provider_key()
    {
        using var database = TestDatabase.Create();
        var connection = database.Setup();

        Assert.False(connection.IsConfigured);

        await connection.SaveSignedInAsync(
            new LlmSetup(LlmProvider.OmniRoute, null, "gho_stored", "gpt-4o"),
            new LlmAuthState(AuthMethod.GitHubCopilot, "octocat", DateTime.UtcNow));

        Assert.True(connection.IsConfigured);
        Assert.True(connection.IsSignedIn);
        Assert.Equal("gho_stored", connection.Current.ApiKey);
        Assert.Equal(AuthMethod.GitHubCopilot, connection.Auth.Method);
        Assert.Equal("Sign in with GitHub Copilot · octocat", connection.Auth.Describe());

        // A restart reads the same state back, so "signed in" survives the process.
        var reopened = database.Setup();
        Assert.True(reopened.IsSignedIn);
        Assert.Equal("octocat", reopened.Auth.Login);

        // Pasting a key later replaces the sign-in instead of lingering as a badge.
        await connection.SaveAsync(new LlmSetup(LlmProvider.OmniRoute, null, "sk-pasted-key-0001", "gpt-4o"));
        Assert.False(connection.Auth.IsDeviceFlow);
        Assert.Equal("sk-pasted-key-0001", connection.Current.ApiKey);

        await connection.SignOutAsync();
        Assert.Null(connection.Current.ApiKey);
        Assert.False(connection.IsSignedIn);
    }

    [Fact]
    public async Task A_pasted_key_is_not_mistaken_for_a_sign_in()
    {
        using var database = TestDatabase.Create();
        var connection = database.Setup();

        await connection.SaveAsync(new LlmSetup(LlmProvider.OmniRoute, null, "sk-pasted-key-0002", "gpt-4o"));

        Assert.True(connection.IsConfigured);
        Assert.False(connection.IsSignedIn);
        Assert.Null(connection.Auth.Describe());
        Assert.Equal(AuthMethod.ApiKey, connection.Auth.Method);
    }

    /// <summary>A tiny scripted HTTP handler: one reply per matching path, in order.</summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Queue<(HttpStatusCode Status, string Body)>> _routes = new(StringComparer.OrdinalIgnoreCase);

        public List<(string Url, string Body)> Requests { get; } = new();

        public ScriptedHandler Json(string path, string body)
            => Reply(path, HttpStatusCode.OK, body);

        public ScriptedHandler Status(string path, HttpStatusCode status, string body)
            => Reply(path, status, body);

        private ScriptedHandler Reply(string path, HttpStatusCode status, string body)
        {
            if (!_routes.TryGetValue(path, out var queue))
            {
                queue = new Queue<(HttpStatusCode, string)>();
                _routes[path] = queue;
            }

            queue.Enqueue((status, body));
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            Requests.Add((url, body));

            foreach (var route in _routes)
            {
                if (!url.Contains(route.Key, StringComparison.OrdinalIgnoreCase) || route.Value.Count == 0)
                {
                    continue;
                }

                var reply = route.Value.Count > 1 ? route.Value.Dequeue() : route.Value.Peek();

                return new HttpResponseMessage(reply.Status)
                {
                    Content = new StringContent(reply.Body, Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        }
    }
}
