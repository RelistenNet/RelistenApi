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
        var playedAt = DateTimeOffset.Parse("2026-06-20T11:00:00Z");
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
                    playlistEntryUuid,
                    playedAt: playedAt)
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
                id AS "HistoryUuid",
                user_id AS "UserUuid",
                client_event_uuid AS "ClientEventUuid",
                source_track_uuid AS "SourceTrackUuid",
                source_uuid AS "SourceUuid",
                playlist_uuid AS "PlaylistUuid",
                playlist_entry_uuid AS "PlaylistEntryUuid",
                played_at AS "PlayedAt",
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
        var queuedAggregate = await connection.QuerySingleAsync<CatalogAggregateQueueRow>(
            """
            SELECT
                playback_history_id AS "PlaybackHistoryUuid",
                source_track_uuid AS "SourceTrackUuid",
                source_uuid AS "SourceUuid",
                played_at AS "PlayedAt",
                platform AS "Platform",
                processed_at AS "ProcessedAt"
            FROM user_data.playback_history_catalog_play_queue
            WHERE playback_history_id = @PlaybackHistoryUuid
            """,
            new { PlaybackHistoryUuid = stored.HistoryUuid });
        var queuedAggregateCount = await connection.QuerySingleAsync<int>(
            """
            SELECT count(*)::int
            FROM user_data.playback_history_catalog_play_queue
            WHERE playback_history_id = @PlaybackHistoryUuid
            """,
            new { PlaybackHistoryUuid = stored.HistoryUuid });
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
        stored.PlayedAt.Should().BeCloseTo(playedAt, precision: TimeSpan.FromMilliseconds(1));
        stored.Platform.Should().Be("ios");
        stored.AppVersion.Should().Be("4.2.1");
        UuidTestAssertions.ShouldBeUuidV7(stored.HistoryUuid);
        queuedAggregate.PlaybackHistoryUuid.Should().Be(stored.HistoryUuid);
        queuedAggregate.SourceTrackUuid.Should().Be(sourceTrackUuid);
        queuedAggregate.SourceUuid.Should().Be(sourceUuid);
        queuedAggregate.PlayedAt.Should().BeCloseTo(playedAt, precision: TimeSpan.FromMilliseconds(1));
        queuedAggregate.Platform.Should().Be("ios");
        queuedAggregate.ProcessedAt.Should().BeNull();
        queuedAggregateCount.Should().Be(1);
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
        var sourceTrackUuid = Guid.NewGuid();
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
        var queuedAggregateCount = await connection.QuerySingleAsync<int>(
            """
            SELECT count(*)::int
            FROM user_data.playback_history_catalog_play_queue
            WHERE source_track_uuid = @SourceTrackUuid
              AND source_uuid = @SourceUuid
            """,
            new
            {
                SourceTrackUuid = sourceTrackUuid,
                SourceUuid = sourceUuid
            });

        historyCount.Should().Be(0);
        keyCount.Should().Be(0);
        queuedAggregateCount.Should().Be(0);
    }

    [Test]
    public async Task Recent_ShouldReturnNewestRowsForCurrentUserOnly()
    {
        await EnsurePostgresOrIgnore();
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        var owner = await SignIn(client);
        var otherUser = await SignIn(client);
        var olderEventUuid = Guid.NewGuid();
        var newerEventUuid = Guid.NewGuid();
        var excludedEventUuid = Guid.NewGuid();
        var otherUserEventUuid = Guid.NewGuid();
        var deviceId = $"history-recent-{Guid.NewGuid():N}";
        var sourceUuid = Guid.NewGuid();
        var newerSourceTrackUuid = Guid.NewGuid();
        var playlistUuid = Guid.NewGuid();
        var playlistEntryUuid = Guid.NewGuid();
        var blockUuid = Guid.NewGuid();

        await UploadBatch(
            client,
            owner.AccessToken,
            new PlaybackHistoryBatchRequest
            {
                Events =
                [
                    NewHistoryEvent(
                        olderEventUuid,
                        deviceId,
                        sourceTrackUuid: Guid.NewGuid(),
                        sourceUuid,
                        playlistUuid: null,
                        playlistEntryUuid: null,
                        playedAt: DateTimeOffset.Parse("2026-06-20T10:00:00Z")),
                    NewHistoryEvent(
                        newerEventUuid,
                        deviceId,
                        sourceTrackUuid: newerSourceTrackUuid,
                        sourceUuid,
                        playlistUuid,
                        playlistEntryUuid,
                        blockUuid,
                        blockPosition: 7,
                        playedAt: DateTimeOffset.Parse("2026-06-20T10:02:00Z")),
                    NewHistoryEvent(
                        excludedEventUuid,
                        deviceId,
                        sourceTrackUuid: Guid.NewGuid(),
                        sourceUuid,
                        playlistUuid: null,
                        playlistEntryUuid: null,
                        playedAt: DateTimeOffset.Parse("2026-06-20T09:59:00Z"))
                ]
            });
        await UploadBatch(
            client,
            otherUser.AccessToken,
            new PlaybackHistoryBatchRequest
            {
                Events =
                [
                    NewHistoryEvent(
                        otherUserEventUuid,
                        deviceId,
                        sourceTrackUuid: Guid.NewGuid(),
                        sourceUuid,
                        playlistUuid: null,
                        playlistEntryUuid: null,
                        playedAt: DateTimeOffset.Parse("2026-06-20T10:03:00Z"))
                ]
            });

        var recent = await ReadRecent(client, owner.AccessToken, limit: 2);
        var recentJson = await ReadRecentJson(client, owner.AccessToken, limit: 2);
        var firstItemJson = (JObject)((JArray)recentJson["items"]!)[0]!;

        recent.Items.Select(item => item.ClientEventUuid)
            .Should()
            .Equal(newerEventUuid, olderEventUuid);
        recent.Items[0].SourceTrackUuid.Should().Be(newerSourceTrackUuid);
        recent.Items[0].PlaylistUuid.Should().Be(playlistUuid);
        recent.Items[0].PlaylistEntryUuid.Should().Be(playlistEntryUuid);
        recent.Items[0].BlockUuid.Should().Be(blockUuid);
        recent.Items[0].BlockPosition.Should().Be(7);
        recent.Items.Should().NotContain(item => item.ClientEventUuid == excludedEventUuid);
        recent.Items.Should().NotContain(item => item.ClientEventUuid == otherUserEventUuid);
        firstItemJson["client_event_uuid"]!.Value<string>().Should().Be(newerEventUuid.ToString());
        firstItemJson["source_track_uuid"]!.Value<string>().Should().Be(newerSourceTrackUuid.ToString());
        firstItemJson["playlist_uuid"]!.Value<string>().Should().Be(playlistUuid.ToString());
        firstItemJson["playlist_entry_uuid"]!.Value<string>().Should().Be(playlistEntryUuid.ToString());
        firstItemJson["block_uuid"]!.Value<string>().Should().Be(blockUuid.ToString());
        firstItemJson["block_position"]!.Value<int>().Should().Be(7);
        firstItemJson.Property("device_id").Should().BeNull();
        firstItemJson.Property("platform").Should().BeNull();
        firstItemJson.Property("app_version").Should().BeNull();
    }

    [TestCase("101")]
    [TestCase("0")]
    [TestCase("not-a-number")]
    public async Task Recent_ShouldRejectInvalidLimit(string limit)
    {
        await EnsurePostgresOrIgnore();
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        var auth = await SignIn(client);
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v3/library/history/recent?limit={limit}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        JObject.Parse(body)["error"]!.Value<string>().Should().Be("invalid_history_limit");
    }

    private static PlaybackHistoryEventRequest NewHistoryEvent(
        Guid clientEventUuid,
        string deviceId,
        Guid sourceTrackUuid,
        Guid sourceUuid,
        Guid? playlistUuid,
        Guid? playlistEntryUuid,
        Guid? blockUuid = null,
        int? blockPosition = null,
        DateTimeOffset? playedAt = null)
    {
        return new PlaybackHistoryEventRequest
        {
            ClientEventUuid = clientEventUuid,
            SourceTrackUuid = sourceTrackUuid,
            SourceUuid = sourceUuid,
            PlaylistUuid = playlistUuid,
            PlaylistEntryUuid = playlistEntryUuid,
            BlockUuid = blockUuid,
            BlockPosition = blockPosition,
            PlayedAt = playedAt ?? DateTimeOffset.UtcNow.AddSeconds(-5),
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

    private static async Task<PlaybackHistoryRecentResponse> ReadRecent(
        HttpClient client,
        string accessToken,
        int? limit)
    {
        var body = await ReadRecentBody(client, accessToken, limit);
        return JsonConvert.DeserializeObject<PlaybackHistoryRecentResponse>(
            body,
            UserLibraryJson.SerializerSettings)!;
    }

    private static async Task<JObject> ReadRecentJson(
        HttpClient client,
        string accessToken,
        int? limit)
    {
        return JObject.Parse(await ReadRecentBody(client, accessToken, limit));
    }

    private static async Task<string> ReadRecentBody(
        HttpClient client,
        string accessToken,
        int? limit)
    {
        var requestUri = limit.HasValue
            ? $"/api/v3/library/history/recent?limit={limit.Value}"
            : "/api/v3/library/history/recent";
        var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return body;
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
        public required Guid HistoryUuid { get; init; }
        public required Guid UserUuid { get; init; }
        public required Guid ClientEventUuid { get; init; }
        public required Guid SourceTrackUuid { get; init; }
        public required Guid SourceUuid { get; init; }
        public Guid? PlaylistUuid { get; init; }
        public Guid? PlaylistEntryUuid { get; init; }
        public required DateTimeOffset PlayedAt { get; init; }
        public required string DeviceId { get; init; }
        public required string Platform { get; init; }
        public required string AppVersion { get; init; }
    }

    private sealed class CatalogAggregateQueueRow
    {
        public required Guid PlaybackHistoryUuid { get; init; }
        public required Guid SourceTrackUuid { get; init; }
        public required Guid SourceUuid { get; init; }
        public required DateTimeOffset PlayedAt { get; init; }
        public required string Platform { get; init; }
        public DateTimeOffset? ProcessedAt { get; init; }
    }
}
