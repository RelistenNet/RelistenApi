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
public class UserLibraryPlaylistTests
{
    [Test]
    public async Task CreateAndAddTrack_ShouldPersistDuplicateSourceTracks()
    {
        await EnsurePostgresOrIgnore();
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        var auth = await SignIn(client);
        var playlist = await CreatePlaylist(client, auth.AccessToken);
        var sourceTrackUuid = Guid.NewGuid();
        var firstEntryUuid = Guid.NewGuid();
        var secondEntryUuid = Guid.NewGuid();

        var first = await ApplyOperation(
            client,
            auth.AccessToken,
            playlist.PlaylistUuid,
            new PlaylistOperationRequest
            {
                Op = "add_track",
                IdempotencyKey = Guid.NewGuid(),
                EntryUuid = firstEntryUuid,
                SourceTrackUuid = sourceTrackUuid
            });
        var second = await ApplyOperation(
            client,
            auth.AccessToken,
            playlist.PlaylistUuid,
            new PlaylistOperationRequest
            {
                Op = "add_track",
                IdempotencyKey = Guid.NewGuid(),
                EntryUuid = secondEntryUuid,
                SourceTrackUuid = sourceTrackUuid,
                BaseRevision = first.ResultRevision
            });

        second.ResultStatus.Should().Be("applied");
        second.Playlist.CurrentRevision.Should().Be(2);
        second.Playlist.Entries.Should().HaveCount(2);
        second.Playlist.Entries.Select(entry => entry.SourceTrackUuid)
            .Should()
            .OnlyContain(uuid => uuid == sourceTrackUuid);
        second.Playlist.Entries.Select(entry => entry.PlaylistEntryUuid)
            .Should()
            .BeEquivalentTo([firstEntryUuid, secondEntryUuid]);
        second.Playlist.Entries.Select(entry => entry.Position)
            .Should()
            .Equal("0000000001", "0000000002");
    }

    [Test]
    public async Task AddTracksAsBlock_ShouldUseIntegerBlockPositionsAndReplayIdempotently()
    {
        await EnsurePostgresOrIgnore();
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        var auth = await SignIn(client);
        var playlist = await CreatePlaylist(client, auth.AccessToken);
        var blockUuid = Guid.NewGuid();
        var operation = new PlaylistOperationRequest
        {
            Op = "add_tracks_as_block",
            IdempotencyKey = Guid.NewGuid(),
            BlockUuid = blockUuid,
            EntryUuids = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()],
            SourceTrackUuids = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()]
        };

        var applied = await ApplyOperation(client, auth.AccessToken, playlist.PlaylistUuid, operation);
        var replay = await ApplyOperation(client, auth.AccessToken, playlist.PlaylistUuid, operation);

        applied.ResultStatus.Should().Be("applied");
        applied.ResultRevision.Should().Be(1);
        applied.Playlist.Entries.Should().HaveCount(3);
        applied.Playlist.Entries.Select(entry => entry.BlockUuid)
            .Should()
            .OnlyContain(uuid => uuid == blockUuid);
        applied.Playlist.Entries.Select(entry => entry.BlockPosition)
            .Should()
            .Equal(0, 1, 2);
        replay.ResultStatus.Should().Be("noop_already_applied");
        replay.ResultRevision.Should().Be(1);
        replay.Playlist.Entries.Should().HaveCount(3);
    }

    [Test]
    public async Task List_ShouldIncludePersistedEntries()
    {
        await EnsurePostgresOrIgnore();
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        var auth = await SignIn(client);
        var playlist = await CreatePlaylist(client, auth.AccessToken);
        var sourceTrackUuid = Guid.NewGuid();
        await ApplyOperation(
            client,
            auth.AccessToken,
            playlist.PlaylistUuid,
            new PlaylistOperationRequest
            {
                Op = "add_track",
                IdempotencyKey = Guid.NewGuid(),
                EntryUuid = Guid.NewGuid(),
                SourceTrackUuid = sourceTrackUuid
            });

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v3/library/playlists");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var playlists = JsonConvert.DeserializeObject<IReadOnlyList<PlaylistResponse>>(
            body,
            UserLibraryJson.SerializerSettings)!;
        playlists.Should().ContainSingle();
        playlists[0].Entries.Should().ContainSingle();
        playlists[0].Entries[0].SourceTrackUuid.Should().Be(sourceTrackUuid);
    }

    [Test]
    public async Task Operations_ShouldReplayConcurrentSameIdempotencyKey()
    {
        await EnsurePostgresOrIgnore();
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        var auth = await SignIn(client);
        var playlist = await CreatePlaylist(client, auth.AccessToken);
        var operation = new PlaylistOperationRequest
        {
            Op = "add_track",
            IdempotencyKey = Guid.NewGuid(),
            EntryUuid = Guid.NewGuid(),
            SourceTrackUuid = Guid.NewGuid()
        };

        var results = await Task.WhenAll(
            ApplyOperation(client, auth.AccessToken, playlist.PlaylistUuid, operation),
            ApplyOperation(client, auth.AccessToken, playlist.PlaylistUuid, operation));

        results.Select(result => result.ResultStatus)
            .Should()
            .BeEquivalentTo(["applied", "noop_already_applied"]);
        results.Should().OnlyContain(result => result.Playlist.Entries.Count == 1);
        results.Select(result => result.ResultRevision)
            .Should()
            .OnlyContain(revision => revision == 1);
    }

    [Test]
    public async Task Operations_ShouldRejectSameIdempotencyKeyWithDifferentPayload()
    {
        await EnsurePostgresOrIgnore();
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        var auth = await SignIn(client);
        var playlist = await CreatePlaylist(client, auth.AccessToken);
        var idempotencyKey = Guid.NewGuid();
        await ApplyOperation(
            client,
            auth.AccessToken,
            playlist.PlaylistUuid,
            new PlaylistOperationRequest
            {
                Op = "add_track",
                IdempotencyKey = idempotencyKey,
                EntryUuid = Guid.NewGuid(),
                SourceTrackUuid = Guid.NewGuid()
            });

        var error = await ApplyOperationExpectingError(
            client,
            auth.AccessToken,
            playlist.PlaylistUuid,
            new PlaylistOperationRequest
            {
                Op = "add_track",
                IdempotencyKey = idempotencyKey,
                EntryUuid = Guid.NewGuid(),
                SourceTrackUuid = Guid.NewGuid()
            });

        error.Should().Be("idempotency_key_conflict");
    }

    [Test]
    public async Task Operations_ShouldRequireNonEmptyIdempotencyKey()
    {
        await EnsurePostgresOrIgnore();
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        var auth = await SignIn(client);
        var playlist = await CreatePlaylist(client, auth.AccessToken);

        var missingKey = await ApplyRawOperationExpectingError(
            client,
            auth.AccessToken,
            playlist.PlaylistUuid,
            $$"""
            {
              "op": "add_track",
              "entry_uuid": "{{Guid.NewGuid()}}",
              "source_track_uuid": "{{Guid.NewGuid()}}"
            }
            """);
        var emptyKey = await ApplyOperationExpectingError(
            client,
            auth.AccessToken,
            playlist.PlaylistUuid,
            new PlaylistOperationRequest
            {
                Op = "add_track",
                IdempotencyKey = Guid.Empty,
                EntryUuid = Guid.NewGuid(),
                SourceTrackUuid = Guid.NewGuid()
            });

        missingKey.Should().Be("invalid_idempotency_key");
        emptyKey.Should().Be("invalid_idempotency_key");
    }

    [Test]
    public async Task Operations_ShouldReturnDeterministicErrorsForClientUuidConflicts()
    {
        await EnsurePostgresOrIgnore();
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        var auth = await SignIn(client);
        var playlist = await CreatePlaylist(client, auth.AccessToken);
        var entryUuid = Guid.NewGuid();
        var blockUuid = Guid.NewGuid();

        await ApplyOperation(
            client,
            auth.AccessToken,
            playlist.PlaylistUuid,
            new PlaylistOperationRequest
            {
                Op = "add_track",
                IdempotencyKey = Guid.NewGuid(),
                EntryUuid = entryUuid,
                SourceTrackUuid = Guid.NewGuid()
            });
        var duplicateEntry = await ApplyOperationExpectingError(
            client,
            auth.AccessToken,
            playlist.PlaylistUuid,
            new PlaylistOperationRequest
            {
                Op = "add_track",
                IdempotencyKey = Guid.NewGuid(),
                EntryUuid = entryUuid,
                SourceTrackUuid = Guid.NewGuid()
            });
        await ApplyOperation(
            client,
            auth.AccessToken,
            playlist.PlaylistUuid,
            new PlaylistOperationRequest
            {
                Op = "add_tracks_as_block",
                IdempotencyKey = Guid.NewGuid(),
                BlockUuid = blockUuid,
                EntryUuids = [Guid.NewGuid(), Guid.NewGuid()],
                SourceTrackUuids = [Guid.NewGuid(), Guid.NewGuid()]
            });
        var duplicateBlock = await ApplyOperationExpectingError(
            client,
            auth.AccessToken,
            playlist.PlaylistUuid,
            new PlaylistOperationRequest
            {
                Op = "add_tracks_as_block",
                IdempotencyKey = Guid.NewGuid(),
                BlockUuid = blockUuid,
                EntryUuids = [Guid.NewGuid(), Guid.NewGuid()],
                SourceTrackUuids = [Guid.NewGuid(), Guid.NewGuid()]
            });

        duplicateEntry.Should().Be("entry_uuid_conflict");
        duplicateBlock.Should().Be("block_uuid_conflict");
    }

    [Test]
    public async Task Operations_ShouldRejectBlockUuidReusedAcrossPlaylists()
    {
        await EnsurePostgresOrIgnore();
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        var auth = await SignIn(client);
        var firstPlaylist = await CreatePlaylist(client, auth.AccessToken);
        var secondPlaylist = await CreatePlaylist(client, auth.AccessToken);
        var blockUuid = Guid.NewGuid();

        await ApplyOperation(
            client,
            auth.AccessToken,
            firstPlaylist.PlaylistUuid,
            new PlaylistOperationRequest
            {
                Op = "add_tracks_as_block",
                IdempotencyKey = Guid.NewGuid(),
                BlockUuid = blockUuid,
                EntryUuids = [Guid.NewGuid(), Guid.NewGuid()],
                SourceTrackUuids = [Guid.NewGuid(), Guid.NewGuid()]
            });
        var error = await ApplyOperationExpectingError(
            client,
            auth.AccessToken,
            secondPlaylist.PlaylistUuid,
            new PlaylistOperationRequest
            {
                Op = "add_tracks_as_block",
                IdempotencyKey = Guid.NewGuid(),
                BlockUuid = blockUuid,
                EntryUuids = [Guid.NewGuid(), Guid.NewGuid()],
                SourceTrackUuids = [Guid.NewGuid(), Guid.NewGuid()]
            });

        error.Should().Be("block_uuid_conflict");
    }

    [Test]
    public async Task Operations_ShouldRejectEntryUuidReusedAcrossPlaylists()
    {
        await EnsurePostgresOrIgnore();
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        var auth = await SignIn(client);
        var firstPlaylist = await CreatePlaylist(client, auth.AccessToken);
        var secondPlaylist = await CreatePlaylist(client, auth.AccessToken);
        var entryUuid = Guid.NewGuid();

        await ApplyOperation(
            client,
            auth.AccessToken,
            firstPlaylist.PlaylistUuid,
            new PlaylistOperationRequest
            {
                Op = "add_track",
                IdempotencyKey = Guid.NewGuid(),
                EntryUuid = entryUuid,
                SourceTrackUuid = Guid.NewGuid()
            });
        var error = await ApplyOperationExpectingError(
            client,
            auth.AccessToken,
            secondPlaylist.PlaylistUuid,
            new PlaylistOperationRequest
            {
                Op = "add_track",
                IdempotencyKey = Guid.NewGuid(),
                EntryUuid = entryUuid,
                SourceTrackUuid = Guid.NewGuid()
            });

        error.Should().Be("entry_uuid_conflict");
    }

    [Test]
    public async Task Create_ShouldRejectDuplicateClientPlaylistUuid()
    {
        await EnsurePostgresOrIgnore();
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        var auth = await SignIn(client);
        var playlistUuid = Guid.NewGuid();
        await CreatePlaylist(client, auth.AccessToken, playlistUuid);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v3/library/playlists");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        request.Content = JsonContent(new CreatePlaylistRequest
        {
            PlaylistUuid = playlistUuid,
            Name = "Duplicate Playlist"
        });
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        JObject.Parse(body)["error"]!.Value<string>().Should().Be("playlist_uuid_conflict");
    }

    [Test]
    public async Task Operations_ShouldRejectPlacementUntilReorderSupportExists()
    {
        await EnsurePostgresOrIgnore();
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        var auth = await SignIn(client);
        var playlist = await CreatePlaylist(client, auth.AccessToken);

        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v3/library/playlists/{playlist.PlaylistUuid}/operations");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        request.Content = JsonContent(new PlaylistOperationRequest
        {
            Op = "add_track",
            IdempotencyKey = Guid.NewGuid(),
            EntryUuid = Guid.NewGuid(),
            SourceTrackUuid = Guid.NewGuid(),
            Placement = new PlaylistPlacementRequest { PositionHint = "aM" }
        });
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        JObject.Parse(body)["error"]!.Value<string>().Should().Be("unsupported_placement");
    }

    [Test]
    public async Task AddTrack_ShouldRejectBlockFields()
    {
        await EnsurePostgresOrIgnore();
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        var auth = await SignIn(client);
        var playlist = await CreatePlaylist(client, auth.AccessToken);

        var error = await ApplyOperationExpectingError(
            client,
            auth.AccessToken,
            playlist.PlaylistUuid,
            new PlaylistOperationRequest
            {
                Op = "add_track",
                IdempotencyKey = Guid.NewGuid(),
                EntryUuid = Guid.NewGuid(),
                SourceTrackUuid = Guid.NewGuid(),
                BlockUuid = Guid.NewGuid()
            });

        error.Should().Be("invalid_operation");
    }

    [Test]
    public async Task Get_ShouldBeOwnerScoped()
    {
        await EnsurePostgresOrIgnore();
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        var owner = await SignIn(client);
        var other = await SignIn(client);
        var playlist = await CreatePlaylist(client, owner.AccessToken);

        var ownerRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v3/library/playlists/{playlist.PlaylistUuid}");
        ownerRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", owner.AccessToken);
        var ownerResponse = await client.SendAsync(ownerRequest);
        var otherRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v3/library/playlists/{playlist.PlaylistUuid}");
        otherRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", other.AccessToken);
        var otherResponse = await client.SendAsync(otherRequest);

        ownerResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        otherResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Playlists_ShouldSerializeSnakeCaseAndRequireAuth()
    {
        await EnsurePostgresOrIgnore();
        await using var factory = NewFactory();
        using var client = factory.CreateClient();

        var unauthenticated = await client.PostAsync(
            "/api/v3/library/playlists",
            JsonContent(new CreatePlaylistRequest { Name = "No Auth" }));
        unauthenticated.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        unauthenticated.Headers.CacheControl!.NoStore.Should().BeTrue();

        var auth = await SignIn(client);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v3/library/playlists");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        request.Content = JsonContent(new CreatePlaylistRequest { Name = "Snake Case" });
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        body.Should().Contain("\"playlist_uuid\"");
        body.Should().Contain("\"short_id\"");
        body.Should().Contain("\"current_revision\"");
        body.Should().NotContain("PlaylistUuid");
    }

    private static async Task<AuthTokenResponse> SignIn(HttpClient client)
    {
        return await PostJson<DevelopmentSessionRequest, AuthTokenResponse>(
            client,
            "/api/v3/library/auth/development/session",
            new DevelopmentSessionRequest
            {
                Username = UniqueUsername(),
                DisplayName = "Playlist Test User",
                DeviceId = $"playlist-test-{Guid.NewGuid():N}",
                DeviceName = "Test Device",
                Platform = "ios"
            });
    }

    private static Task<PlaylistResponse> CreatePlaylist(HttpClient client, string accessToken)
    {
        return CreatePlaylist(client, accessToken, Guid.NewGuid());
    }

    private static async Task<PlaylistResponse> CreatePlaylist(
        HttpClient client,
        string accessToken,
        Guid playlistUuid)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v3/library/playlists");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent(new CreatePlaylistRequest
        {
            PlaylistUuid = playlistUuid,
            Name = "Test Playlist",
            Description = "Created by an integration test"
        });
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Created, body);
        return JsonConvert.DeserializeObject<PlaylistResponse>(body, UserLibraryJson.SerializerSettings)!;
    }

    private static async Task<PlaylistOperationResponse> ApplyOperation(
        HttpClient client,
        string accessToken,
        Guid playlistUuid,
        PlaylistOperationRequest operation)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v3/library/playlists/{playlistUuid}/operations");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent(operation);
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        JObject.Parse(body)["playlist"]!["entries"]!.Should().NotBeNull();
        return JsonConvert.DeserializeObject<PlaylistOperationResponse>(
            body,
            UserLibraryJson.SerializerSettings)!;
    }

    private static async Task<string> ApplyOperationExpectingError(
        HttpClient client,
        string accessToken,
        Guid playlistUuid,
        PlaylistOperationRequest operation)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v3/library/playlists/{playlistUuid}/operations");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent(operation);
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        return JObject.Parse(body)["error"]!.Value<string>()!;
    }

    private static async Task<string> ApplyRawOperationExpectingError(
        HttpClient client,
        string accessToken,
        Guid playlistUuid,
        string json)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v3/library/playlists/{playlistUuid}/operations");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        return JObject.Parse(body)["error"]!.Value<string>()!;
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
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["UserData:DatabaseUrl"] = DatabaseUrl
            })
            .Build();
        await using var connection = new UserApiDbService(configuration).CreateConnection();

        try
        {
            await connection.OpenAsync();
        }
        catch
        {
            Assert.Ignore("Local Postgres is not available for playlist integration tests.");
        }
    }

    private static string UniqueUsername()
    {
        return $"playlist_{Guid.NewGuid():N}"[..30];
    }

    private static string DatabaseUrl =>
        Environment.GetEnvironmentVariable("DATABASE_URL") ??
        "postgresql://relisten:local_dev_password@127.0.0.1:15432/relisten_db";

    private const string TestSigningKey = "test-access-token-signing-key-with-more-than-32-bytes";
}
