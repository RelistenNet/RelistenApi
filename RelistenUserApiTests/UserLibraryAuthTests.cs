using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
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
public class UserLibraryAuthTests
{
    [Test]
    public async Task ProviderCallback_ShouldCreateUserSessionAndBearerAccess()
    {
        var fakeProvider = new FakeProviderVerifier();
        fakeProvider.AddSubject("google", "provider-token", "google-subject-1");
        await using var factory = NewFactory(fakeProvider);
        using var client = factory.CreateClient();

        var auth = await SignIn(client);

        auth.User.Username.Should().Be("relisten_user");
        auth.RefreshToken.Should().Contain(".");
        auth.AccessToken.Should().Contain(".");

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v3/library/users/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        var me = await client.SendAsync(request);
        var body = await me.Content.ReadAsStringAsync();

        me.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("\"username\":\"relisten_user\"");
        body.Should().Contain("\"user_uuid\"");
        body.Should().NotContain("UserUuid");
    }

    [Test]
    public async Task DevelopmentSession_ShouldIssueRealTokensInDevelopment()
    {
        await using var factory = NewFactory(new FakeProviderVerifier(), environment: "Development");
        using var client = factory.CreateClient();

        var auth = await PostJson<DevelopmentSessionRequest, AuthTokenResponse>(
            client,
            "/api/v3/library/auth/development/session",
            new DevelopmentSessionRequest
            {
                Username = "ios_simulator",
                DisplayName = "iOS Simulator",
                DeviceId = "simulator-device",
                DeviceName = "iPhone 16 Pro",
                Platform = "ios"
            });

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v3/library/users/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        var me = await client.SendAsync(request);

        me.StatusCode.Should().Be(HttpStatusCode.OK);
        auth.RefreshToken.Should().Contain(".");
        auth.Session.DeviceId.Should().Be("simulator-device");
    }

    [Test]
    public async Task DevelopmentSession_ShouldBeClosedOutsideDevelopmentOrTest()
    {
        await using var factory = NewFactory(new FakeProviderVerifier(), environment: "Production");
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/api/v3/library/auth/development/session",
            JsonContent(new DevelopmentSessionRequest
            {
                Username = "ios_simulator",
                DisplayName = "iOS Simulator",
                DeviceId = "simulator-device",
                DeviceName = "iPhone 16 Pro",
                Platform = "ios"
            }));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Refresh_ShouldRotateRefreshTokenAndRejectReuse()
    {
        var fakeProvider = new FakeProviderVerifier();
        fakeProvider.AddSubject("google", "provider-token", "google-subject-1");
        await using var factory = NewFactory(fakeProvider);
        using var client = factory.CreateClient();
        var auth = await SignIn(client);

        var refresh = await PostJson<RefreshTokenRequest, AuthTokenResponse>(
            client,
            "/api/v3/library/auth/refresh",
            new RefreshTokenRequest { RefreshToken = auth.RefreshToken });

        refresh.RefreshToken.Should().NotBe(auth.RefreshToken);

        var reuse = await client.PostAsync(
            "/api/v3/library/auth/refresh",
            JsonContent(new RefreshTokenRequest { RefreshToken = auth.RefreshToken }));

        reuse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var body = await reuse.Content.ReadAsStringAsync();
        JObject.Parse(body)["error"]!.Value<string>().Should().Be("refresh_token_reuse_detected");

        var afterReuse = await client.PostAsync(
            "/api/v3/library/auth/refresh",
            JsonContent(new RefreshTokenRequest { RefreshToken = refresh.RefreshToken }));
        afterReuse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Refresh_ShouldNotRevokeSessionWhenRotatedTokenHasWrongSecret()
    {
        var fakeProvider = new FakeProviderVerifier();
        fakeProvider.AddSubject("google", "provider-token", "google-subject-1");
        await using var factory = NewFactory(fakeProvider);
        using var client = factory.CreateClient();
        var auth = await SignIn(client);

        var refresh = await PostJson<RefreshTokenRequest, AuthTokenResponse>(
            client,
            "/api/v3/library/auth/refresh",
            new RefreshTokenRequest { RefreshToken = auth.RefreshToken });
        var oldSelector = auth.RefreshToken.Split('.')[0];

        var forgedReuse = await client.PostAsync(
            "/api/v3/library/auth/refresh",
            JsonContent(new RefreshTokenRequest { RefreshToken = $"{oldSelector}.wrong-secret" }));

        forgedReuse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var stillValid = await PostJson<RefreshTokenRequest, AuthTokenResponse>(
            client,
            "/api/v3/library/auth/refresh",
            new RefreshTokenRequest { RefreshToken = refresh.RefreshToken });
        stillValid.User.UserUuid.Should().Be(auth.User.UserUuid);
    }

    [Test]
    public async Task Logout_ShouldRevokeSessionForRefreshToken()
    {
        var fakeProvider = new FakeProviderVerifier();
        fakeProvider.AddSubject("google", "provider-token", "google-subject-1");
        await using var factory = NewFactory(fakeProvider);
        using var client = factory.CreateClient();
        var auth = await SignIn(client);

        var logout = await client.PostAsync(
            "/api/v3/library/auth/logout",
            JsonContent(new LogoutRequest { RefreshToken = auth.RefreshToken }));
        logout.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var refresh = await client.PostAsync(
            "/api/v3/library/auth/refresh",
            JsonContent(new RefreshTokenRequest { RefreshToken = auth.RefreshToken }));
        refresh.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Logout_ShouldRejectMalformedOrWrongRefreshTokenWithoutRevokingSession()
    {
        var fakeProvider = new FakeProviderVerifier();
        fakeProvider.AddSubject("google", "provider-token", "google-subject-1");
        await using var factory = NewFactory(fakeProvider);
        using var client = factory.CreateClient();
        var auth = await SignIn(client);

        var malformed = await client.PostAsync(
            "/api/v3/library/auth/logout",
            JsonContent(new LogoutRequest { RefreshToken = "not-a-refresh-token" }));

        malformed.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        JObject.Parse(await malformed.Content.ReadAsStringAsync())["error"]!
            .Value<string>()
            .Should()
            .Be("invalid_refresh_token");

        var selector = auth.RefreshToken.Split('.')[0];
        var wrongSecret = await client.PostAsync(
            "/api/v3/library/auth/logout",
            JsonContent(new LogoutRequest { RefreshToken = $"{selector}.wrong-secret" }));

        wrongSecret.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var refresh = await PostJson<RefreshTokenRequest, AuthTokenResponse>(
            client,
            "/api/v3/library/auth/refresh",
            new RefreshTokenRequest { RefreshToken = auth.RefreshToken });

        refresh.User.UserUuid.Should().Be(auth.User.UserUuid);
    }

    [Test]
    public async Task ProviderCallback_ShouldRejectProvidersOutsideAppleAndGoogle()
    {
        var fakeProvider = new FakeProviderVerifier();
        fakeProvider.AddSubject("github", "provider-token", "github-subject-1");
        await using var factory = NewFactory(fakeProvider);
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/api/v3/library/auth/callback/github",
            JsonContent(new ProviderSignInRequest
            {
                ProviderToken = "provider-token",
                Username = "relisten_user",
                DisplayName = "Relisten User",
                DeviceId = "device-1",
                DeviceName = "iPhone",
                Platform = "ios"
            }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        JObject.Parse(await response.Content.ReadAsStringAsync())["error"]!
            .Value<string>()
            .Should()
            .Be("provider_not_supported");
    }

    [Test]
    public async Task BearerAccess_ShouldRejectSessionForDifferentUser()
    {
        var fakeProvider = new FakeProviderVerifier();
        fakeProvider.AddSubject("google", "provider-token", "google-subject-1");
        await using var factory = NewFactory(fakeProvider);
        using var client = factory.CreateClient();
        var auth = await SignIn(client);

        var otherUser = new UserAccount
        {
            UserUuid = Guid.NewGuid(),
            Username = "other_user",
            DisplayName = "Other User",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        var session = new UserSession
        {
            SessionUuid = auth.Session.SessionUuid,
            UserUuid = otherUser.UserUuid,
            DeviceId = "device-1",
            DeviceName = "iPhone",
            Platform = "ios",
            LastUsedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        };
        var mismatchedToken = new AccessTokenService(Options.Create(new UserAuthOptions
        {
            AccessTokenSigningKey = TestSigningKey
        })).Issue(otherUser, session, DateTimeOffset.UtcNow);

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v3/library/users/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", mismatchedToken.Plaintext);
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static async Task<AuthTokenResponse> SignIn(HttpClient client)
    {
        return await PostJson<ProviderSignInRequest, AuthTokenResponse>(
            client,
            "/api/v3/library/auth/callback/google",
            new ProviderSignInRequest
            {
                ProviderToken = "provider-token",
                Username = "relisten_user",
                DisplayName = "Relisten User",
                DeviceId = "device-1",
                DeviceName = "iPhone",
                Platform = "ios"
            });
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
        FakeProviderVerifier fakeProvider,
        string environment = "Test")
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment(environment);
                builder.ConfigureTestServices(services =>
                {
                    services.AddSingleton<IAuthProviderVerifier>(fakeProvider);
                    services.AddSingleton<IUserAuthStore, InMemoryUserAuthStore>();
                });
                builder.ConfigureAppConfiguration((_, configuration) =>
                {
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["UserAuth:AccessTokenSigningKey"] = TestSigningKey
                    });
                });
            });
    }

    private const string TestSigningKey = "test-access-token-signing-key-with-more-than-32-bytes";
}
