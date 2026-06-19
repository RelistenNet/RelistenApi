using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Relisten.UserApi.Models;
using Relisten.UserApi.Services;
using Relisten.UserApi.Serialization;

namespace RelistenUserApiTests;

[TestFixture]
public class UserLibrarySessionsTests
{
    [Test]
    public async Task Sessions_ShouldListAndRevokeCurrentUserSessions()
    {
        var fakeProvider = new FakeProviderVerifier();
        fakeProvider.AddSubject("apple", "provider-token", "apple-subject-1");
        await using var factory = NewFactory(fakeProvider);
        using var client = factory.CreateClient();
        var auth = await SignIn(client);

        var sessionsRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v3/library/users/me/sessions");
        sessionsRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        var sessionsResponse = await client.SendAsync(sessionsRequest);
        var sessions = JsonConvert.DeserializeObject<List<UserSessionResponse>>(
            await sessionsResponse.Content.ReadAsStringAsync(),
            UserLibraryJson.SerializerSettings)!;

        sessionsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        sessions.Should().ContainSingle();
        sessions[0].DeviceId.Should().Be("device-1");

        var revokeRequest = new HttpRequestMessage(
            HttpMethod.Delete,
            $"/api/v3/library/users/me/sessions/{auth.Session.SessionUuid}");
        revokeRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        var revokeResponse = await client.SendAsync(revokeRequest);

        revokeResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var afterRevokeRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v3/library/users/me");
        afterRevokeRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        var afterRevokeResponse = await client.SendAsync(afterRevokeRequest);

        afterRevokeResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Sessions_ShouldListOnlyActiveSessions()
    {
        var fakeProvider = new FakeProviderVerifier();
        fakeProvider.AddSubject("apple", "provider-token", "apple-subject-1");
        await using var factory = NewFactory(fakeProvider);
        using var client = factory.CreateClient();
        var first = await SignIn(client);
        var second = await SignIn(client);

        var revokeRequest = new HttpRequestMessage(
            HttpMethod.Delete,
            $"/api/v3/library/users/me/sessions/{first.Session.SessionUuid}");
        revokeRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", second.AccessToken);
        var revokeResponse = await client.SendAsync(revokeRequest);

        revokeResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var sessionsRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v3/library/users/me/sessions");
        sessionsRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", second.AccessToken);
        var sessionsResponse = await client.SendAsync(sessionsRequest);
        var sessions = JsonConvert.DeserializeObject<List<UserSessionResponse>>(
            await sessionsResponse.Content.ReadAsStringAsync(),
            UserLibraryJson.SerializerSettings)!;

        sessionsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        sessions.Should().ContainSingle();
        sessions[0].SessionUuid.Should().Be(second.Session.SessionUuid);
    }

    private static async Task<AuthTokenResponse> SignIn(HttpClient client)
    {
        var response = await client.PostAsync(
            "/api/v3/library/auth/callback/apple",
            new StringContent(
                JsonConvert.SerializeObject(new ProviderSignInRequest
                {
                    ProviderToken = "provider-token",
                    Username = "relisten_user",
                    DisplayName = "Relisten User",
                    DeviceId = "device-1",
                    DeviceName = "iPhone",
                    Platform = "ios"
                }, UserLibraryJson.SerializerSettings),
                System.Text.Encoding.UTF8,
                "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonConvert.DeserializeObject<AuthTokenResponse>(
            await response.Content.ReadAsStringAsync(),
            UserLibraryJson.SerializerSettings)!;
    }

    private static WebApplicationFactory<Program> NewFactory(FakeProviderVerifier fakeProvider)
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
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
