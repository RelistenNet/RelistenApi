using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Relisten.UserApi.Configuration;
using Relisten.UserApi.Models;
using Relisten.UserApi.Services;
using Relisten.UserApi.Serialization;

namespace RelistenUserApiTests;

[TestFixture]
public class UserLibraryWebAuthTests
{
    [Test]
    public async Task GoogleStart_ShouldRedirectToGoogleAndSetProtectedStateCookie()
    {
        await using var factory = NewFactory();
        using var client = NewManualCookieClient(factory);

        var response = await client.GetAsync("/api/v3/library/auth/web/google/start?return_url=/account");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.GetLeftPart(UriPartial.Path).Should().Be("https://accounts.example.test/oauth");
        var query = QueryHelpers.ParseQuery(response.Headers.Location.Query);
        query["client_id"].ToString().Should().Be("google-web-client-id");
        query["redirect_uri"].ToString().Should().Be("http://localhost/api/v3/library/auth/web/google/callback");
        query["response_type"].ToString().Should().Be("code");
        query["scope"].ToString().Should().Be("openid profile email");
        query["state"].ToString().Should().NotBeNullOrWhiteSpace();
        query["nonce"].ToString().Should().NotBeNullOrWhiteSpace();

        var stateCookie = SetCookie(response, "relisten_oauth_state");
        stateCookie.Raw.ToLowerInvariant().Should().Contain("httponly");
        stateCookie.Raw.ToLowerInvariant().Should().Contain("samesite=lax");
        stateCookie.Value.Should().NotContain("test-google-oauth-client-secret");
    }

    [Test]
    public async Task GoogleCallback_ShouldCreateHttpOnlyWebSessionForLibraryRoutes()
    {
        var fakeProvider = new FakeProviderVerifier();
        fakeProvider.AddSubject("google", "provider-id-token", "google-subject-1", "Google User");
        var codeExchanger = new FakeWebOAuthCodeExchanger("provider-id-token");
        await using var factory = NewFactory(fakeProvider, codeExchanger);
        using var client = NewManualCookieClient(factory);

        var start = await client.GetAsync("/api/v3/library/auth/web/google/start?return_url=/account");
        var state = QueryHelpers.ParseQuery(start.Headers.Location!.Query)["state"].ToString();
        var oauthStateCookie = SetCookie(start, "relisten_oauth_state");
        var callback = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v3/library/auth/web/google/callback?code=web-code-1&state={Uri.EscapeDataString(state)}");
        callback.Headers.Add("Cookie", CookieHeader(("relisten_oauth_state", oauthStateCookie.Value)));

        var response = await client.SendAsync(callback);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().Should().Be("/account");
        codeExchanger.Exchanges.Should().ContainSingle().Which.Code.Should().Be("web-code-1");
        codeExchanger.Exchanges.Single().RedirectUri.Should().Be(
            "http://localhost/api/v3/library/auth/web/google/callback");
        var sessionCookie = SetCookie(response, "relisten_user_session");
        sessionCookie.Raw.ToLowerInvariant().Should().Contain("httponly");
        var csrfCookie = SetCookie(response, "relisten_user_csrf");
        csrfCookie.Raw.ToLowerInvariant().Should().NotContain("httponly");

        var meRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v3/library/users/me");
        meRequest.Headers.Add("Cookie", CookieHeader(("relisten_user_session", sessionCookie.Value)));
        var me = await client.SendAsync(meRequest);
        var meBody = await me.Content.ReadAsStringAsync();

        me.StatusCode.Should().Be(HttpStatusCode.OK);
        meBody.Should().Contain("\"display_name\":\"Google User\"");
        meBody.Should().Contain("\"username\":\"google_");
        meBody.Should().Contain("\"user_uuid\"");
        meBody.Should().NotContain("UserUuid");
    }

    [Test]
    public async Task GoogleCallback_ShouldRejectMismatchedOAuthStateBeforeCodeExchange()
    {
        var codeExchanger = new FakeWebOAuthCodeExchanger("provider-id-token");
        await using var factory = NewFactory(codeExchanger: codeExchanger);
        using var client = NewManualCookieClient(factory);
        var start = await client.GetAsync("/api/v3/library/auth/web/google/start?return_url=/account");
        var state = QueryHelpers.ParseQuery(start.Headers.Location!.Query)["state"].ToString();
        var oauthStateCookie = SetCookie(start, "relisten_oauth_state");
        var callback = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v3/library/auth/web/google/callback?code=web-code-1&state={state}-tampered");
        callback.Headers.Add("Cookie", CookieHeader(("relisten_oauth_state", oauthStateCookie.Value)));

        var response = await client.SendAsync(callback);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        JObject.Parse(await response.Content.ReadAsStringAsync())["error"]!
            .Value<string>()
            .Should()
            .Be("invalid_oauth_state");
        codeExchanger.Exchanges.Should().BeEmpty();
    }

    [Test]
    public async Task WebCookieMutations_ShouldRequireCsrfHeaderAndAllowedOrigin()
    {
        await using var signedIn = await SignInWebUser();

        var missingCsrf = new HttpRequestMessage(HttpMethod.Post, "/api/v3/library/auth/web/logout");
        missingCsrf.Headers.Add("Cookie", signedIn.CookieHeader);
        missingCsrf.Headers.Add("Origin", "http://localhost");
        var rejected = await signedIn.Client.SendAsync(missingCsrf);

        rejected.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var stillSignedIn = new HttpRequestMessage(HttpMethod.Get, "/api/v3/library/users/me");
        stillSignedIn.Headers.Add("Cookie", signedIn.CookieHeader);
        (await signedIn.Client.SendAsync(stillSignedIn)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task WebCookieMutations_ShouldRejectWrongOriginOrCsrfToken()
    {
        await using var signedIn = await SignInWebUser();
        var wrongOrigin = new HttpRequestMessage(HttpMethod.Post, "/api/v3/library/auth/web/logout");
        wrongOrigin.Headers.Add("Cookie", signedIn.CookieHeader);
        wrongOrigin.Headers.Add("Origin", "https://evil.example");
        wrongOrigin.Headers.Add("X-Relisten-Csrf", signedIn.CsrfToken);

        var wrongOriginResponse = await signedIn.Client.SendAsync(wrongOrigin);

        var wrongCsrf = new HttpRequestMessage(HttpMethod.Post, "/api/v3/library/auth/web/logout");
        wrongCsrf.Headers.Add("Cookie", signedIn.CookieHeader);
        wrongCsrf.Headers.Add("Origin", "http://localhost");
        wrongCsrf.Headers.Add("X-Relisten-Csrf", "wrong-csrf-token");
        var wrongCsrfResponse = await signedIn.Client.SendAsync(wrongCsrf);

        wrongOriginResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        wrongCsrfResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task BearerAuth_ShouldTakePrecedenceOverWebCookieForUnsafeRequests()
    {
        await using var signedIn = await SignInWebUser();
        var bearerAuth = await PostJson<ProviderSignInRequest, AuthTokenResponse>(
            signedIn.Client,
            "/api/v3/library/auth/callback/google",
            new ProviderSignInRequest
            {
                ProviderToken = "provider-id-token",
                Username = "bearer_user",
                DisplayName = "Bearer User",
                DeviceId = "device-1",
                DeviceName = "Browser",
                Platform = "web"
            });
        var reauthenticate = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/v3/library/auth/reauthenticate/google");
        reauthenticate.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerAuth.AccessToken);
        reauthenticate.Headers.Add("Cookie", signedIn.CookieHeader);
        reauthenticate.Content = JsonContent(new ProviderReauthenticationRequest
        {
            ProviderToken = "provider-id-token"
        });

        var response = await signedIn.Client.SendAsync(reauthenticate);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task WebLogout_ShouldRevokeBackingSession()
    {
        await using var signedIn = await SignInWebUser();
        var logout = new HttpRequestMessage(HttpMethod.Post, "/api/v3/library/auth/web/logout");
        logout.Headers.Add("Cookie", signedIn.CookieHeader);
        logout.Headers.Add("Origin", "http://localhost");
        logout.Headers.Add("X-Relisten-Csrf", signedIn.CsrfToken);

        var logoutResponse = await signedIn.Client.SendAsync(logout);
        var staleCookieRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v3/library/users/me");
        staleCookieRequest.Headers.Add("Cookie", signedIn.CookieHeader);
        var staleCookieResponse = await signedIn.Client.SendAsync(staleCookieRequest);

        logoutResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
        staleCookieResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task GoogleStart_ShouldRejectOpenRedirectReturnUrl()
    {
        await using var factory = NewFactory();
        using var client = NewManualCookieClient(factory);

        var response = await client.GetAsync(
            "/api/v3/library/auth/web/google/start?return_url=https%3A%2F%2Fevil.example");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        JObject.Parse(await response.Content.ReadAsStringAsync())["error"]!
            .Value<string>()
            .Should()
            .Be("invalid_return_url");
    }

    [Test]
    public async Task GoogleStart_ShouldFailClosedWhenWebClientIdIsMissing()
    {
        await using var factory = NewFactory(configureGoogleClient: false);
        using var client = NewManualCookieClient(factory);

        var response = await client.GetAsync("/api/v3/library/auth/web/google/start");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        JObject.Parse(await response.Content.ReadAsStringAsync())["error"]!
            .Value<string>()
            .Should()
            .Be("provider_not_configured");
    }

    [Test]
    public async Task GoogleStart_ShouldFailClosedWhenWebAudienceIsMissing()
    {
        await using var factory = NewFactory(configureGoogleAudience: false);
        using var client = NewManualCookieClient(factory);

        var response = await client.GetAsync("/api/v3/library/auth/web/google/start");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        JObject.Parse(await response.Content.ReadAsStringAsync())["error"]!
            .Value<string>()
            .Should()
            .Be("provider_not_configured");
    }

    [Test]
    public async Task GoogleCodeExchanger_ShouldSendAuthorizationCodeGrantAndParseIdToken()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"id_token":"provider-id-token"}""")
        });
        var exchanger = new GoogleWebOAuthCodeExchanger(
            new HttpClient(handler),
            Options.Create(new UserAuthOptions
            {
                Google = new ProviderTokenValidationOptions
                {
                    ClientId = "google-web-client-id",
                    ClientSecret = "test-google-oauth-client-secret",
                    TokenEndpoint = "https://oauth2.example.test/token"
                }
            }));

        var token = await exchanger.ExchangeCode(
            "google",
            "authorization-code",
            "https://user.relisten.net/api/v3/library/auth/web/google/callback");

        token.IdToken.Should().Be("provider-id-token");
        handler.Request!.Method.Should().Be(HttpMethod.Post);
        handler.Request.RequestUri!.ToString().Should().Be("https://oauth2.example.test/token");
        var form = QueryHelpers.ParseQuery(handler.Body!);
        form["grant_type"].ToString().Should().Be("authorization_code");
        form["code"].ToString().Should().Be("authorization-code");
        form["redirect_uri"].ToString().Should()
            .Be("https://user.relisten.net/api/v3/library/auth/web/google/callback");
        form["client_id"].ToString().Should().Be("google-web-client-id");
        form["client_secret"].ToString().Should().Be("test-google-oauth-client-secret");
    }

    private static async Task<SignedInWebUser> SignInWebUser()
    {
        var fakeProvider = new FakeProviderVerifier();
        fakeProvider.AddSubject("google", "provider-id-token", "google-subject-1", "Google User");
        var codeExchanger = new FakeWebOAuthCodeExchanger("provider-id-token");
        var factory = NewFactory(fakeProvider, codeExchanger);
        var client = NewManualCookieClient(factory);
        var start = await client.GetAsync("/api/v3/library/auth/web/google/start?return_url=/account");
        var state = QueryHelpers.ParseQuery(start.Headers.Location!.Query)["state"].ToString();
        var oauthStateCookie = SetCookie(start, "relisten_oauth_state");
        var callback = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v3/library/auth/web/google/callback?code=web-code-1&state={Uri.EscapeDataString(state)}");
        callback.Headers.Add("Cookie", CookieHeader(("relisten_oauth_state", oauthStateCookie.Value)));
        var response = await client.SendAsync(callback);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var sessionCookie = SetCookie(response, "relisten_user_session").Value;
        var csrfToken = SetCookie(response, "relisten_user_csrf").Value;
        return new SignedInWebUser(
            factory,
            client,
            CookieHeader(("relisten_user_session", sessionCookie), ("relisten_user_csrf", csrfToken)),
            csrfToken);
    }

    private static HttpClient NewManualCookieClient(WebApplicationFactory<Program> factory)
    {
        return factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false
        });
    }

    private static CookieParts SetCookie(HttpResponseMessage response, string name)
    {
        var raw = response.Headers.GetValues("Set-Cookie")
            .Last(value => value.StartsWith($"{name}=", StringComparison.Ordinal));
        var value = raw[($"{name}=").Length..].Split(';')[0];
        value.Should().NotBeNullOrWhiteSpace();
        return new CookieParts(raw, value);
    }

    private static string CookieHeader(params (string Name, string Value)[] cookies)
    {
        return string.Join("; ", cookies.Select(cookie => $"{cookie.Name}={cookie.Value}"));
    }

    private static async Task<TResponse> PostJson<TRequest, TResponse>(
        HttpClient client,
        string path,
        TRequest request)
    {
        var response = await client.PostAsync(path, JsonContent(request));
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonConvert.DeserializeObject<TResponse>(body, UserLibraryJson.SerializerSettings)!;
    }

    private static StringContent JsonContent<T>(T value)
    {
        return new StringContent(
            JsonConvert.SerializeObject(value, UserLibraryJson.SerializerSettings),
            System.Text.Encoding.UTF8,
            "application/json");
    }

    private static WebApplicationFactory<Program> NewFactory(
        FakeProviderVerifier? fakeProvider = null,
        IWebOAuthCodeExchanger? codeExchanger = null,
        bool configureGoogleClient = true,
        bool configureGoogleAudience = true)
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureTestServices(services =>
                {
                    services.AddSingleton<IAuthProviderVerifier>(fakeProvider ?? new FakeProviderVerifier());
                    services.AddSingleton<IUserAuthStore, InMemoryUserAuthStore>();
                    if (codeExchanger != null)
                    {
                        services.AddSingleton(codeExchanger);
                    }
                });
                builder.ConfigureAppConfiguration((_, configuration) =>
                {
                    var values = new Dictionary<string, string?>
                    {
                        ["UserAuth:AccessTokenSigningKey"] = TestSigningKey,
                        ["UserAuth:Google:AuthorizationEndpoint"] = "https://accounts.example.test/oauth",
                        ["UserAuth:Google:TokenEndpoint"] = "https://oauth2.example.test/token",
                        ["UserAuth:Google:RedirectUri"] = "http://localhost/api/v3/library/auth/web/google/callback",
                        ["UserAuth:Web:SecureCookies"] = "false"
                    };
                    if (configureGoogleClient)
                    {
                        values["UserAuth:Google:ClientId"] = "google-web-client-id";
                        values["UserAuth:Google:ClientSecret"] = "test-google-oauth-client-secret";
                        if (configureGoogleAudience)
                        {
                            values["UserAuth:Google:Audiences:0"] = "google-web-client-id";
                        }
                    }

                    configuration.AddInMemoryCollection(values);
                });
            });
    }

    private const string TestSigningKey = "test-access-token-signing-key-with-more-than-32-bytes";

    private sealed record CookieParts(string Raw, string Value);

    private sealed record SignedInWebUser(
        WebApplicationFactory<Program> Factory,
        HttpClient Client,
        string CookieHeader,
        string CsrfToken) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Factory.DisposeAsync();
        }
    }

    private sealed class FakeWebOAuthCodeExchanger : IWebOAuthCodeExchanger
    {
        private readonly string _idToken;

        public FakeWebOAuthCodeExchanger(string idToken)
        {
            _idToken = idToken;
        }

        public List<ExchangeRecord> Exchanges { get; } = new();

        public Task<WebOAuthTokenSet> ExchangeCode(string provider, string code, string redirectUri)
        {
            provider.Should().Be("google");
            Exchanges.Add(new ExchangeRecord(code, redirectUri));
            return Task.FromResult(new WebOAuthTokenSet(_idToken));
        }
    }

    private sealed record ExchangeRecord(string Code, string RedirectUri);

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public CapturingHandler(HttpResponseMessage response)
        {
            _response = response;
        }

        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content == null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return _response;
        }
    }
}
