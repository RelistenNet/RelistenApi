using System.Net;
using System.Net.Http.Headers;
using Dapper;
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
public class UserLibraryHistoryTests
{
    [Test]
    public async Task Batch_ShouldPersistPlaylistAttributionAndDeduplicateRetries()
    {
        await EnsurePostgresOrIgnore();
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        var auth = await SignIn(client);
        var playlist = await CreatePlaylist(client, auth.AccessToken);
        var playlistEntryUuid = Guid.NewGuid();
        var sourceTrackUuid = Guid.NewGuid();
        await ApplyOperation(
            client,
            auth.AccessToken,
            playlist.PlaylistUuid,
            new PlaylistOperationRequest
            {
                Op = "add_track",
                IdempotencyKey = Guid.NewGuid(),
                EntryUuid = playlistEntryUuid,
                SourceTrackUuid = sourceTrackUuid
            });
        var clientEventUuid = Guid.NewGuid();
        var deviceId = $"history-test-{Guid.NewGuid():N}";
        var sourceUuid = Guid.NewGuid();
        var request = new PlaybackHistoryBatchRequest
        {
            Events =
            [
                NewHistoryEvent(
                    clientEventUuid,
                    deviceId,
                    sourceTrackUuid,
                    sourceUuid,
                    playlist.PlaylistUuid,
                    playlistEntryUuid)
            ]
        };

        var first = await UploadBatch(client, auth.AccessToken, request);
        var replay = await UploadBatch(client, auth.AccessToken, request);

        first.HistoryEnabled.Should().BeTrue();
        first.AcceptedCount.Should().Be(1);
        first.DuplicateCount.Should().Be(0);
        first.Results.Should().ContainSingle(result =>
            result.ClientEventUuid == clientEventUuid &&
            result.Status == "accepted");
        replay.AcceptedCount.Should().Be(0);
        replay.DuplicateCount.Should().Be(1);
        replay.Results.Should().ContainSingle(result =>
            result.ClientEventUuid == clientEventUuid &&
            result.Status == "duplicate");

        await using var connection = NewDbService().CreateConnection();
        var stored = await connection.QuerySingleAsync<PlaybackHistoryRow>(
            """
            SELECT
                user_id AS "UserUuid",
                client_event_uuid AS "ClientEventUuid",
                source_track_uuid AS "SourceTrackUuid",
                source_uuid AS "SourceUuid",
                playlist_uuid AS "PlaylistUuid",
                playlist_entry_uuid AS "PlaylistEntryUuid",
                device_id AS "DeviceId",
                platform AS "Platform",
                app_version AS "AppVersion"
            FROM user_data.playback_history
            WHERE user_id = @UserUuid
              AND device_id = @DeviceId
              AND client_event_uuid = @ClientEventUuid
            """,
            new
            {
                auth.User.UserUuid,
                DeviceId = deviceId,
                ClientEventUuid = clientEventUuid
            });
        var keyCount = await connection.QuerySingleAsync<int>(
            """
            SELECT count(*)::int
            FROM user_data.playback_history_ingest_keys
            WHERE user_id = @UserUuid
              AND device_id = @DeviceId
              AND client_event_uuid = @ClientEventUuid
            """,
            new
            {
                auth.User.UserUuid,
                DeviceId = deviceId,
                ClientEventUuid = clientEventUuid
            });

        stored.UserUuid.Should().Be(auth.User.UserUuid);
        stored.SourceTrackUuid.Should().Be(sourceTrackUuid);
        stored.SourceUuid.Should().Be(sourceUuid);
        stored.PlaylistUuid.Should().Be(playlist.PlaylistUuid);
        stored.PlaylistEntryUuid.Should().Be(playlistEntryUuid);
        stored.Platform.Should().Be("ios");
        stored.AppVersion.Should().Be("4.2.1");
        keyCount.Should().Be(1);
    }

    [Test]
    public async Task Batch_ShouldNoOpWhenHistoryIsDisabled()
    {
        await EnsurePostgresOrIgnore();
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        var auth = await SignIn(client);
        await UpdateSettings(client, auth.AccessToken, JObject.Parse("""{ "history_enabled": false }"""));
        var clientEventUuid = Guid.NewGuid();
        var deviceId = $"history-disabled-{Guid.NewGuid():N}";
        var request = new PlaybackHistoryBatchRequest
        {
            Events =
            [
                NewHistoryEvent(
                    clientEventUuid,
                    deviceId,
                    sourceTrackUuid: Guid.NewGuid(),
                    sourceUuid: Guid.NewGuid(),
                    playlistUuid: null,
                    playlistEntryUuid: null)
            ]
        };

        var response = await UploadBatch(client, auth.AccessToken, request);

        response.HistoryEnabled.Should().BeFalse();
        response.AcceptedCount.Should().Be(0);
        response.DuplicateCount.Should().Be(0);
        response.Results.Should().ContainSingle(result =>
            result.ClientEventUuid == clientEventUuid &&
            result.Status == "rejected_history_disabled");

        await using var connection = NewDbService().CreateConnection();
        var historyCount = await connection.QuerySingleAsync<int>(
            """
            SELECT count(*)::int
            FROM user_data.playback_history
            WHERE user_id = @UserUuid
              AND device_id = @DeviceId
              AND client_event_uuid = @ClientEventUuid
            """,
            new
            {
                auth.User.UserUuid,
                DeviceId = deviceId,
                ClientEventUuid = clientEventUuid
            });
        var keyCount = await connection.QuerySingleAsync<int>(
            """
            SELECT count(*)::int
            FROM user_data.playback_history_ingest_keys
            WHERE user_id = @UserUuid
              AND device_id = @DeviceId
              AND client_event_uuid = @ClientEventUuid
            """,
            new
            {
                auth.User.UserUuid,
                DeviceId = deviceId,
                ClientEventUuid = clientEventUuid
            });

        historyCount.Should().Be(0);
        keyCount.Should().Be(0);
    }

    private static PlaybackHistoryEventRequest NewHistoryEvent(
        Guid clientEventUuid,
        string deviceId,
        Guid sourceTrackUuid,
        Guid sourceUuid,
        Guid? playlistUuid,
        Guid? playlistEntryUuid)
    {
        return new PlaybackHistoryEventRequest
        {
            ClientEventUuid = clientEventUuid,
            SourceTrackUuid = sourceTrackUuid,
            SourceUuid = sourceUuid,
            PlaylistUuid = playlistUuid,
            PlaylistEntryUuid = playlistEntryUuid,
            PlayedAt = DateTimeOffset.UtcNow.AddSeconds(-5),
            Platform = "ios",
            AppVersion = "4.2.1",
            DeviceId = deviceId
        };
    }

    private static async Task<AuthTokenResponse> SignIn(HttpClient client)
    {
        return await PostJson<DevelopmentSessionRequest, AuthTokenResponse>(
            client,
            "/api/v3/library/auth/development/session",
            new DevelopmentSessionRequest
            {
                Username = UniqueUsername(),
                DisplayName = "History Test User",
                DeviceId = $"history-auth-{Guid.NewGuid():N}",
                DeviceName = "Test Device",
                Platform = "ios"
            },
            accessToken: null,
            expectedStatus: HttpStatusCode.OK);
    }

    private static async Task<PlaylistResponse> CreatePlaylist(HttpClient client, string accessToken)
    {
        return await PostJson<CreatePlaylistRequest, PlaylistResponse>(
            client,
            "/api/v3/library/playlists",
            new CreatePlaylistRequest
            {
                PlaylistUuid = Guid.NewGuid(),
                Name = "History Playlist"
            },
            accessToken,
            HttpStatusCode.Created);
    }

    private static async Task<PlaylistOperationResponse> ApplyOperation(
        HttpClient client,
        string accessToken,
        Guid playlistUuid,
        PlaylistOperationRequest operation)
    {
        return await PostJson<PlaylistOperationRequest, PlaylistOperationResponse>(
            client,
            $"/api/v3/library/playlists/{playlistUuid}/operations",
            operation,
            accessToken,
            HttpStatusCode.OK);
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

    private static async Task<PlaybackHistoryBatchResponse> UploadBatch(
        HttpClient client,
        string accessToken,
        PlaybackHistoryBatchRequest request)
    {
        return await PostJson<PlaybackHistoryBatchRequest, PlaybackHistoryBatchResponse>(
            client,
            "/api/v3/library/history/batch",
            request,
            accessToken,
            HttpStatusCode.OK);
    }

    private static async Task<TResponse> PostJson<TRequest, TResponse>(
        HttpClient client,
        string path,
        TRequest request,
        string? accessToken,
        HttpStatusCode expectedStatus)
    {
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, path);
        if (accessToken != null)
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        httpRequest.Content = JsonContent(request);
        var response = await client.SendAsync(httpRequest);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(expectedStatus, body);
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
            Assert.Ignore("Local Postgres is not available for history integration tests.");
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
        return $"history_{Guid.NewGuid():N}"[..30];
    }

    private static string DatabaseUrl =>
        Environment.GetEnvironmentVariable("DATABASE_URL") ??
        "postgresql://relisten:local_dev_password@127.0.0.1:15432/relisten_db";

    private const string TestSigningKey = "test-access-token-signing-key-with-more-than-32-bytes";

    private sealed class PlaybackHistoryRow
    {
        public required Guid UserUuid { get; init; }
        public required Guid ClientEventUuid { get; init; }
        public required Guid SourceTrackUuid { get; init; }
        public required Guid SourceUuid { get; init; }
        public Guid? PlaylistUuid { get; init; }
        public Guid? PlaylistEntryUuid { get; init; }
        public required string DeviceId { get; init; }
        public required string Platform { get; init; }
        public required string AppVersion { get; init; }
    }
}
