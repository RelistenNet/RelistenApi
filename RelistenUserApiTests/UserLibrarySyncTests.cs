using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Relisten.UserApi.Models;
using Relisten.UserApi.Services;
using Relisten.UserApi.Serialization;

namespace RelistenUserApiTests;

[TestFixture]
public class UserLibrarySyncTests
{
    [Test]
    public async Task Favorites_ShouldSupportAllMobileEntityTypesAndSyncTombstones()
    {
        await EnsurePostgresOrIgnore();
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        var auth = await SignIn(client);
        var favorites = new Dictionary<string, Guid>
        {
            ["artist"] = Guid.NewGuid(),
            ["show"] = Guid.NewGuid(),
            ["source"] = Guid.NewGuid(),
            ["track"] = Guid.NewGuid(),
            ["tour"] = Guid.NewGuid(),
            ["song"] = Guid.NewGuid()
        };

        foreach (var favorite in favorites)
        {
            var response = await PutFavorite(client, auth.AccessToken, favorite.Key, favorite.Value);
            response.EntityType.Should().Be(favorite.Key);
            response.EntityUuid.Should().Be(favorite.Value);
        }

        var listed = await ListFavorites(client, auth.AccessToken);
        listed.Select(favorite => favorite.EntityType)
            .Should()
            .BeEquivalentTo(["artist", "show", "source", "track", "tour", "song"]);

        var deleted = await DeleteFavorite(client, auth.AccessToken, "source", favorites["source"]);
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var afterDelete = await ListFavorites(client, auth.AccessToken);
        afterDelete.Select(favorite => favorite.EntityType).Should().NotContain("source");

        var sync = await PullSync(client, auth.AccessToken);
        sync.Changes
            .Where(change => change.ResourceType == "favorite")
            .Select(change => change.Favorite!.EntityType)
            .Should()
            .BeEquivalentTo(["artist", "show", "track", "tour", "song"]);
        sync.Tombstones.Should().ContainSingle(tombstone =>
            tombstone.ResourceType == "favorite" &&
            tombstone.EntityType == "source" &&
            tombstone.EntityUuid == favorites["source"]);

        var idle = await PullSync(client, auth.AccessToken, sync.NextCursor);
        idle.Changes.Should().BeEmpty();
        idle.Tombstones.Should().BeEmpty();
    }

    [Test]
    public async Task Settings_ShouldRoundTripJsonAndSyncAsAccountWideState()
    {
        await EnsurePostgresOrIgnore();
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        var auth = await SignIn(client);

        var empty = await GetSettings(client, auth.AccessToken);
        empty.Settings.Should().BeEquivalentTo(new JObject());
        empty.UpdatedAt.Should().BeNull();

        var updated = await UpdateSettings(
            client,
            auth.AccessToken,
            JObject.Parse(
                """
                {
                  "history_enabled": true,
                  "autoplay_deep_links": "wifi_only",
                  "source_selection": { "prefer_soundboard": true }
                }
                """));

        updated.UpdatedAt.Should().NotBeNull();
        updated.Settings["history_enabled"]!.Value<bool>().Should().BeTrue();
        updated.Settings["source_selection"]!["prefer_soundboard"]!.Value<bool>().Should().BeTrue();

        var readBack = await GetSettings(client, auth.AccessToken);
        readBack.Settings.Should().BeEquivalentTo(updated.Settings);

        var sync = await PullSync(client, auth.AccessToken);
        var settingsChange = sync.Changes.Should().ContainSingle(change => change.ResourceType == "settings").Subject;
        settingsChange.Settings!.Settings.Should().BeEquivalentTo(updated.Settings);
        settingsChange.Settings.UpdatedAt.Should().Be(updated.UpdatedAt);
        sync.Tombstones.Should().BeEmpty();
    }

    [Test]
    public async Task IdempotentRetries_ShouldNotCreateNewSyncChanges()
    {
        await EnsurePostgresOrIgnore();
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        var auth = await SignIn(client);
        var favoriteUuid = Guid.NewGuid();
        var settings = JObject.Parse("""{ "history_enabled": true }""");

        await PutFavorite(client, auth.AccessToken, "artist", favoriteUuid);
        var addSync = await PullSync(client, auth.AccessToken);
        await PutFavorite(client, auth.AccessToken, "artist", favoriteUuid);
        var addRetrySync = await PullSync(client, auth.AccessToken, addSync.NextCursor);

        addSync.Changes.Should().ContainSingle(change =>
            change.ResourceType == "favorite" &&
            change.Favorite!.EntityUuid == favoriteUuid);
        addRetrySync.Changes.Should().BeEmpty();
        addRetrySync.Tombstones.Should().BeEmpty();

        await DeleteFavorite(client, auth.AccessToken, "artist", favoriteUuid);
        var deleteSync = await PullSync(client, auth.AccessToken, addSync.NextCursor);
        await DeleteFavorite(client, auth.AccessToken, "artist", favoriteUuid);
        var deleteRetrySync = await PullSync(client, auth.AccessToken, deleteSync.NextCursor);

        deleteSync.Tombstones.Should().ContainSingle(tombstone =>
            tombstone.ResourceType == "favorite" &&
            tombstone.EntityType == "artist" &&
            tombstone.EntityUuid == favoriteUuid);
        deleteRetrySync.Changes.Should().BeEmpty();
        deleteRetrySync.Tombstones.Should().BeEmpty();

        await UpdateSettings(client, auth.AccessToken, settings);
        var settingsSync = await PullSync(client, auth.AccessToken, deleteSync.NextCursor);
        await UpdateSettings(client, auth.AccessToken, settings);
        var settingsRetrySync = await PullSync(client, auth.AccessToken, settingsSync.NextCursor);

        settingsSync.Changes.Should().ContainSingle(change => change.ResourceType == "settings");
        settingsRetrySync.Changes.Should().BeEmpty();
        settingsRetrySync.Tombstones.Should().BeEmpty();
    }

    [Test]
    public async Task SyncEndpoints_ShouldRequireAuthAndRejectInvalidCursor()
    {
        await EnsurePostgresOrIgnore();
        await using var factory = NewFactory();
        using var client = factory.CreateClient();

        var unauthenticated = await client.GetAsync("/api/v3/library/favorites");
        unauthenticated.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        unauthenticated.Headers.CacheControl!.NoStore.Should().BeTrue();

        var auth = await SignIn(client);
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v3/library/sync?cursor=not-a-cursor");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        var invalidCursor = await client.SendAsync(request);
        var body = await invalidCursor.Content.ReadAsStringAsync();

        invalidCursor.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        invalidCursor.Headers.CacheControl!.NoStore.Should().BeTrue();
        JObject.Parse(body)["error"]!.Value<string>().Should().Be("invalid_sync_cursor");
    }

    private static async Task<AuthTokenResponse> SignIn(HttpClient client)
    {
        return await PostJson<DevelopmentSessionRequest, AuthTokenResponse>(
            client,
            "/api/v3/library/auth/development/session",
            new DevelopmentSessionRequest
            {
                Username = UniqueUsername(),
                DisplayName = "Sync Test User",
                DeviceId = $"sync-test-{Guid.NewGuid():N}",
                DeviceName = "Test Device",
                Platform = "ios"
            },
            accessToken: null);
    }

    private static async Task<FavoriteResponse> PutFavorite(
        HttpClient client,
        string accessToken,
        string entityType,
        Guid entityUuid)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/v3/library/favorites/{entityType}/{entityUuid}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonConvert.DeserializeObject<FavoriteResponse>(
            body,
            UserLibraryJson.SerializerSettings)!;
    }

    private static async Task<IReadOnlyList<FavoriteResponse>> ListFavorites(
        HttpClient client,
        string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v3/library/favorites");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonConvert.DeserializeObject<IReadOnlyList<FavoriteResponse>>(
            body,
            UserLibraryJson.SerializerSettings)!;
    }

    private static async Task<HttpResponseMessage> DeleteFavorite(
        HttpClient client,
        string accessToken,
        string entityType,
        Guid entityUuid)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Delete,
            $"/api/v3/library/favorites/{entityType}/{entityUuid}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await client.SendAsync(request);
    }

    private static async Task<UserSettingsResponse> GetSettings(HttpClient client, string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v3/library/settings");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonConvert.DeserializeObject<UserSettingsResponse>(
            body,
            UserLibraryJson.SerializerSettings)!;
    }

    private static async Task<UserSettingsResponse> UpdateSettings(
        HttpClient client,
        string accessToken,
        JObject settings)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, "/api/v3/library/settings");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent(new UpdateUserSettingsRequest { Settings = settings });
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonConvert.DeserializeObject<UserSettingsResponse>(
            body,
            UserLibraryJson.SerializerSettings)!;
    }

    private static async Task<UserLibrarySyncResponse> PullSync(
        HttpClient client,
        string accessToken,
        string? cursor = null)
    {
        var path = cursor == null
            ? "/api/v3/library/sync"
            : $"/api/v3/library/sync?cursor={Uri.EscapeDataString(cursor)}";
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonConvert.DeserializeObject<UserLibrarySyncResponse>(
            body,
            UserLibraryJson.SerializerSettings)!;
    }

    private static async Task<TResponse> PostJson<TRequest, TResponse>(
        HttpClient client,
        string path,
        TRequest request,
        string? accessToken)
    {
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, path);
        if (accessToken != null)
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        httpRequest.Content = JsonContent(request);
        var response = await client.SendAsync(httpRequest);
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

    private static WebApplicationFactory<Program> NewFactory()
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureAppConfiguration((_, configuration) =>
                {
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["UserData:DatabaseUrl"] = DatabaseUrl,
                        ["UserAuth:AccessTokenSigningKey"] = TestSigningKey
                    });
                });
            });
    }

    private static async Task EnsurePostgresOrIgnore()
    {
        await using var connection = NewDbService().CreateConnection();

        try
        {
            await connection.OpenAsync();
        }
        catch
        {
            Assert.Ignore("Local Postgres is not available for sync integration tests.");
        }
    }

    private static UserApiDbService NewDbService()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["UserData:DatabaseUrl"] = DatabaseUrl
            })
            .Build();
        return new UserApiDbService(configuration);
    }

    private static string UniqueUsername()
    {
        return $"sync_{Guid.NewGuid():N}"[..30];
    }

    private static string DatabaseUrl =>
        Environment.GetEnvironmentVariable("DATABASE_URL") ??
        "postgresql://relisten:local_dev_password@127.0.0.1:15432/relisten_db";

    private const string TestSigningKey = "test-access-token-signing-key-with-more-than-32-bytes";
}
