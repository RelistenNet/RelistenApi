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
public class UserLibraryPlaylistTests
{
    [Test]
    public async Task Create_ShouldGenerateUuidV7WhenPlaylistUuidIsOmitted()
    {
        await EnsurePostgresOrIgnore();
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        var auth = await SignIn(client);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v3/library/playlists");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        request.Content = JsonContent(new CreatePlaylistRequest
        {
            Name = "Server Generated Playlist",
            Description = "No client playlist UUID"
        });

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Created, body);
        var playlist = JsonConvert.DeserializeObject<PlaylistResponse>(
            body,
            UserLibraryJson.SerializerSettings)!;
        UuidTestAssertions.ShouldBeUuidV7(playlist.PlaylistUuid);
    }

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
    public async Task AddSourceRangeAsBlock_ShouldResolveCatalogTracksAndReplayIdempotently()
    {
        await EnsurePostgresOrIgnore();
        var catalogSource = await SeedCatalogSource(trackCount: 5);
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        var auth = await SignIn(client);
        var playlist = await CreatePlaylist(client, auth.AccessToken);
        var anchorEntryUuid = Guid.NewGuid();
        await AddTrack(client, auth.AccessToken, playlist.PlaylistUuid, anchorEntryUuid, Guid.NewGuid());
        var blockUuid = Guid.NewGuid();
        var entryUuids = new List<Guid> { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var operation = new PlaylistOperationRequest
        {
            Op = "add_source_range_as_block",
            IdempotencyKey = Guid.NewGuid(),
            BlockUuid = blockUuid,
            EntryUuids = entryUuids,
            SourceUuid = catalogSource.SourceUuid,
            StartTrackPosition = 2,
            EndTrackPosition = 4,
            Placement = new PlaylistPlacementRequest { BeforeEntryUuid = anchorEntryUuid }
        };

        var applied = await ApplyOperation(client, auth.AccessToken, playlist.PlaylistUuid, operation);
        var replay = await ApplyOperation(client, auth.AccessToken, playlist.PlaylistUuid, operation);

        applied.ResultStatus.Should().Be("applied");
        applied.ResultRevision.Should().Be(2);
        applied.Playlist.Entries.Take(3).Select(entry => entry.PlaylistEntryUuid)
            .Should()
            .Equal(entryUuids);
        applied.Playlist.Entries[3].PlaylistEntryUuid.Should().Be(anchorEntryUuid);
        applied.Playlist.Entries.Take(3).Select(entry => entry.SourceTrackUuid)
            .Should()
            .Equal(catalogSource.TrackUuids.Skip(1).Take(3));
        applied.Playlist.Entries.Take(3).Select(entry => entry.BlockUuid)
            .Should()
            .OnlyContain(uuid => uuid == blockUuid);
        applied.Playlist.Entries.Take(3).Select(entry => entry.BlockPosition)
            .Should()
            .Equal(0, 1, 2);
        applied.Playlist.Entries.Select(entry => entry.Position)
            .Should()
            .Equal("0000000001", "0000000002", "0000000003", "0000000004");
        replay.ResultStatus.Should().Be("noop_already_applied");
        replay.ResultRevision.Should().Be(2);
        replay.Playlist.Entries.Take(3).Select(entry => entry.PlaylistEntryUuid)
            .Should()
            .Equal(entryUuids);
    }

    [Test]
    public async Task AddSourceRangeAsBlock_ShouldRejectInvalidCatalogRange()
    {
        await EnsurePostgresOrIgnore();
        var catalogSource = await SeedCatalogSource(trackCount: 2);
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
                Op = "add_source_range_as_block",
                IdempotencyKey = Guid.NewGuid(),
                BlockUuid = Guid.NewGuid(),
                EntryUuids = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()],
                SourceUuid = catalogSource.SourceUuid,
                StartTrackPosition = 1,
                EndTrackPosition = 3
            });

        error.Should().Be("invalid_source_range");
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
    public async Task AddTrack_ShouldHonorPlacementAndReturnCanonicalPositions()
    {
        await EnsurePostgresOrIgnore();
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        var auth = await SignIn(client);
        var playlist = await CreatePlaylist(client, auth.AccessToken);
        var firstEntryUuid = Guid.NewGuid();
        var secondEntryUuid = Guid.NewGuid();
        var insertedEntryUuid = Guid.NewGuid();
        await AddTrack(client, auth.AccessToken, playlist.PlaylistUuid, firstEntryUuid, Guid.NewGuid());
        await AddTrack(client, auth.AccessToken, playlist.PlaylistUuid, secondEntryUuid, Guid.NewGuid());

        var inserted = await ApplyOperation(
            client,
            auth.AccessToken,
            playlist.PlaylistUuid,
            new PlaylistOperationRequest
            {
                Op = "add_track",
                IdempotencyKey = Guid.NewGuid(),
                EntryUuid = insertedEntryUuid,
                SourceTrackUuid = Guid.NewGuid(),
                Placement = new PlaylistPlacementRequest
                {
                    BeforeEntryUuid = secondEntryUuid,
                    PositionHint = "client-hint-is-not-canonical"
                }
            });

        inserted.ResultStatus.Should().Be("applied");
        inserted.Playlist.Entries.Select(entry => entry.PlaylistEntryUuid)
            .Should()
            .Equal(firstEntryUuid, insertedEntryUuid, secondEntryUuid);
        inserted.Playlist.Entries.Select(entry => entry.Position)
            .Should()
            .Equal("0000000001", "0000000002", "0000000003");
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
    public async Task MoveEntry_ShouldReorderByPlaylistEntryUuidNotSourceTrackUuid()
    {
        await EnsurePostgresOrIgnore();
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        var auth = await SignIn(client);
        var playlist = await CreatePlaylist(client, auth.AccessToken);
        var duplicateSourceTrackUuid = Guid.NewGuid();
        var firstEntryUuid = Guid.NewGuid();
        var secondEntryUuid = Guid.NewGuid();
        var tailEntryUuid = Guid.NewGuid();
        await AddTrack(client, auth.AccessToken, playlist.PlaylistUuid, firstEntryUuid, duplicateSourceTrackUuid);
        await AddTrack(client, auth.AccessToken, playlist.PlaylistUuid, secondEntryUuid, duplicateSourceTrackUuid);
        await AddTrack(client, auth.AccessToken, playlist.PlaylistUuid, tailEntryUuid, Guid.NewGuid());

        var moved = await ApplyOperation(
            client,
            auth.AccessToken,
            playlist.PlaylistUuid,
            new PlaylistOperationRequest
            {
                Op = "move_entry",
                IdempotencyKey = Guid.NewGuid(),
                EntryUuid = secondEntryUuid,
                Placement = new PlaylistPlacementRequest { BeforeEntryUuid = firstEntryUuid }
            });

        moved.ResultStatus.Should().Be("applied");
        moved.ResultRevision.Should().Be(4);
        moved.Playlist.Entries.Select(entry => entry.PlaylistEntryUuid)
            .Should()
            .Equal(secondEntryUuid, firstEntryUuid, tailEntryUuid);
        moved.Playlist.Entries.Select(entry => entry.Position)
            .Should()
            .Equal("0000000001", "0000000002", "0000000003");
        moved.Playlist.Entries.Take(2).Select(entry => entry.SourceTrackUuid)
            .Should()
            .OnlyContain(uuid => uuid == duplicateSourceTrackUuid);
    }

    [Test]
    public async Task MoveBlock_ShouldMoveWholeBlockAndPreserveIntegerBlockPositions()
    {
        await EnsurePostgresOrIgnore();
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        var auth = await SignIn(client);
        var playlist = await CreatePlaylist(client, auth.AccessToken);
        var firstStandaloneUuid = Guid.NewGuid();
        var blockEntryUuids = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() };
        var blockUuid = Guid.NewGuid();
        var lastStandaloneUuid = Guid.NewGuid();
        await AddTrack(client, auth.AccessToken, playlist.PlaylistUuid, firstStandaloneUuid, Guid.NewGuid());
        await ApplyOperation(
            client,
            auth.AccessToken,
            playlist.PlaylistUuid,
            new PlaylistOperationRequest
            {
                Op = "add_tracks_as_block",
                IdempotencyKey = Guid.NewGuid(),
                BlockUuid = blockUuid,
                EntryUuids = blockEntryUuids,
                SourceTrackUuids = [Guid.NewGuid(), Guid.NewGuid()]
            });
        await AddTrack(client, auth.AccessToken, playlist.PlaylistUuid, lastStandaloneUuid, Guid.NewGuid());

        var moved = await ApplyOperation(
            client,
            auth.AccessToken,
            playlist.PlaylistUuid,
            new PlaylistOperationRequest
            {
                Op = "move_block",
                IdempotencyKey = Guid.NewGuid(),
                BlockUuid = blockUuid,
                Placement = new PlaylistPlacementRequest { AfterEntryUuid = lastStandaloneUuid }
            });

        moved.ResultStatus.Should().Be("applied");
        moved.Playlist.Entries.Select(entry => entry.PlaylistEntryUuid)
            .Should()
            .Equal(firstStandaloneUuid, lastStandaloneUuid, blockEntryUuids[0], blockEntryUuids[1]);
        moved.Playlist.Entries.Select(entry => entry.Position)
            .Should()
            .Equal("0000000001", "0000000002", "0000000003", "0000000004");
        moved.Playlist.Entries.Skip(2).Select(entry => entry.BlockUuid)
            .Should()
            .OnlyContain(uuid => uuid == blockUuid);
        moved.Playlist.Entries.Skip(2).Select(entry => entry.BlockPosition)
            .Should()
            .Equal(0, 1);
    }

    [Test]
    public async Task MoveEntry_ShouldMoveBlockEntryToStandaloneAndDeleteEmptyBlock()
    {
        await EnsurePostgresOrIgnore();
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        var auth = await SignIn(client);
        var playlist = await CreatePlaylist(client, auth.AccessToken);
        var blockUuid = Guid.NewGuid();
        var entryUuid = Guid.NewGuid();
        await ApplyOperation(
            client,
            auth.AccessToken,
            playlist.PlaylistUuid,
            new PlaylistOperationRequest
            {
                Op = "add_tracks_as_block",
                IdempotencyKey = Guid.NewGuid(),
                BlockUuid = blockUuid,
                EntryUuids = [entryUuid],
                SourceTrackUuids = [Guid.NewGuid()]
            });

        var moved = await ApplyOperation(
            client,
            auth.AccessToken,
            playlist.PlaylistUuid,
            new PlaylistOperationRequest
            {
                Op = "move_entry",
                IdempotencyKey = Guid.NewGuid(),
                EntryUuid = entryUuid,
                Placement = new PlaylistPlacementRequest()
            });

        moved.ResultStatus.Should().Be("applied");
        moved.Playlist.Entries.Should().ContainSingle();
        moved.Playlist.Entries[0].BlockUuid.Should().BeNull();
        moved.Playlist.Entries[0].BlockPosition.Should().BeNull();
        (await CountBlockRows(blockUuid)).Should().Be(0);
    }

    [Test]
    public async Task MoveEntry_ShouldMoveStandaloneEntryIntoExistingBlockAtTargetIndex()
    {
        await EnsurePostgresOrIgnore();
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        var auth = await SignIn(client);
        var playlist = await CreatePlaylist(client, auth.AccessToken);
        var blockEntryUuids = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() };
        var standaloneUuid = Guid.NewGuid();
        var blockUuid = Guid.NewGuid();
        await ApplyOperation(
            client,
            auth.AccessToken,
            playlist.PlaylistUuid,
            new PlaylistOperationRequest
            {
                Op = "add_tracks_as_block",
                IdempotencyKey = Guid.NewGuid(),
                BlockUuid = blockUuid,
                EntryUuids = blockEntryUuids,
                SourceTrackUuids = [Guid.NewGuid(), Guid.NewGuid()]
            });
        await AddTrack(client, auth.AccessToken, playlist.PlaylistUuid, standaloneUuid, Guid.NewGuid());

        var moved = await ApplyOperation(
            client,
            auth.AccessToken,
            playlist.PlaylistUuid,
            new PlaylistOperationRequest
            {
                Op = "move_entry",
                IdempotencyKey = Guid.NewGuid(),
                EntryUuid = standaloneUuid,
                Placement = new PlaylistPlacementRequest
                {
                    TargetBlockUuid = blockUuid,
                    TargetBlockIndex = 1
                }
            });

        moved.ResultStatus.Should().Be("applied");
        moved.Playlist.Entries.Select(entry => entry.PlaylistEntryUuid)
            .Should()
            .Equal(blockEntryUuids[0], standaloneUuid, blockEntryUuids[1]);
        moved.Playlist.Entries.Select(entry => entry.BlockUuid)
            .Should()
            .OnlyContain(uuid => uuid == blockUuid);
        moved.Playlist.Entries.Select(entry => entry.BlockPosition)
            .Should()
            .Equal(0, 1, 2);
        moved.Playlist.Entries.Select(entry => entry.Position)
            .Should()
            .Equal("0000000001", "0000000002", "0000000003");
    }

    [Test]
    public async Task MoveOperations_ShouldRejectAnchorsInsideMovingSet()
    {
        await EnsurePostgresOrIgnore();
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        var auth = await SignIn(client);
        var playlist = await CreatePlaylist(client, auth.AccessToken);
        var firstEntryUuid = Guid.NewGuid();
        var secondEntryUuid = Guid.NewGuid();
        var blockEntryUuids = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() };
        var blockUuid = Guid.NewGuid();
        await AddTrack(client, auth.AccessToken, playlist.PlaylistUuid, firstEntryUuid, Guid.NewGuid());
        await AddTrack(client, auth.AccessToken, playlist.PlaylistUuid, secondEntryUuid, Guid.NewGuid());
        await ApplyOperation(
            client,
            auth.AccessToken,
            playlist.PlaylistUuid,
            new PlaylistOperationRequest
            {
                Op = "add_tracks_as_block",
                IdempotencyKey = Guid.NewGuid(),
                BlockUuid = blockUuid,
                EntryUuids = blockEntryUuids,
                SourceTrackUuids = [Guid.NewGuid(), Guid.NewGuid()]
            });

        var selfEntryAnchor = await ApplyOperationExpectingError(
            client,
            auth.AccessToken,
            playlist.PlaylistUuid,
            new PlaylistOperationRequest
            {
                Op = "move_entry",
                IdempotencyKey = Guid.NewGuid(),
                EntryUuid = firstEntryUuid,
                Placement = new PlaylistPlacementRequest { BeforeEntryUuid = firstEntryUuid }
            });
        var selfBlockAnchor = await ApplyOperationExpectingError(
            client,
            auth.AccessToken,
            playlist.PlaylistUuid,
            new PlaylistOperationRequest
            {
                Op = "move_block",
                IdempotencyKey = Guid.NewGuid(),
                BlockUuid = blockUuid,
                Placement = new PlaylistPlacementRequest { AfterEntryUuid = blockEntryUuids[0] }
            });

        selfEntryAnchor.Should().Be("invalid_placement");
        selfBlockAnchor.Should().Be("invalid_placement");
    }

    [Test]
    public async Task MoveEntry_ShouldRejectPlacementThatBreaksBlockContiguity()
    {
        await EnsurePostgresOrIgnore();
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        var auth = await SignIn(client);
        var playlist = await CreatePlaylist(client, auth.AccessToken);
        var blockEntryUuids = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() };
        var standaloneUuid = Guid.NewGuid();
        await ApplyOperation(
            client,
            auth.AccessToken,
            playlist.PlaylistUuid,
            new PlaylistOperationRequest
            {
                Op = "add_tracks_as_block",
                IdempotencyKey = Guid.NewGuid(),
                BlockUuid = Guid.NewGuid(),
                EntryUuids = blockEntryUuids,
                SourceTrackUuids = [Guid.NewGuid(), Guid.NewGuid()]
            });
        var beforeRejectedMove = await AddTrack(
            client,
            auth.AccessToken,
            playlist.PlaylistUuid,
            standaloneUuid,
            Guid.NewGuid());

        var rejectedOperation = new PlaylistOperationRequest
        {
            Op = "move_entry",
            IdempotencyKey = Guid.NewGuid(),
            EntryUuid = standaloneUuid,
            Placement = new PlaylistPlacementRequest
            {
                AfterEntryUuid = blockEntryUuids[0],
                BeforeEntryUuid = blockEntryUuids[1]
            }
        };
        var rejected = await ApplyOperation(
            client,
            auth.AccessToken,
            playlist.PlaylistUuid,
            rejectedOperation);
        var replay = await ApplyOperation(
            client,
            auth.AccessToken,
            playlist.PlaylistUuid,
            rejectedOperation);

        rejected.ResultStatus.Should().Be("rejected_contiguity");
        rejected.ResultRevision.Should().Be(beforeRejectedMove.ResultRevision);
        replay.ResultStatus.Should().Be("rejected_contiguity");
        replay.ResultRevision.Should().Be(beforeRejectedMove.ResultRevision);
        rejected.Playlist.Entries.Select(entry => entry.PlaylistEntryUuid)
            .Should()
            .Equal(blockEntryUuids[0], blockEntryUuids[1], standaloneUuid);
        rejected.Playlist.Entries.Select(entry => entry.Position)
            .Should()
            .Equal("0000000001", "0000000002", "0000000003");
    }

    [Test]
    public async Task NoopMoveOperations_ShouldReplaySameDeterministicStatus()
    {
        await EnsurePostgresOrIgnore();
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        var auth = await SignIn(client);
        var playlist = await CreatePlaylist(client, auth.AccessToken);
        var missingEntryOperation = new PlaylistOperationRequest
        {
            Op = "move_entry",
            IdempotencyKey = Guid.NewGuid(),
            EntryUuid = Guid.NewGuid(),
            Placement = new PlaylistPlacementRequest()
        };
        var emptyBlockOperation = new PlaylistOperationRequest
        {
            Op = "move_block",
            IdempotencyKey = Guid.NewGuid(),
            BlockUuid = Guid.NewGuid(),
            Placement = new PlaylistPlacementRequest()
        };

        var missingEntry = await ApplyOperation(client, auth.AccessToken, playlist.PlaylistUuid, missingEntryOperation);
        var missingEntryReplay = await ApplyOperation(client, auth.AccessToken, playlist.PlaylistUuid, missingEntryOperation);
        var emptyBlock = await ApplyOperation(client, auth.AccessToken, playlist.PlaylistUuid, emptyBlockOperation);
        var emptyBlockReplay = await ApplyOperation(client, auth.AccessToken, playlist.PlaylistUuid, emptyBlockOperation);

        missingEntry.ResultStatus.Should().Be("noop_entry_missing");
        missingEntryReplay.ResultStatus.Should().Be("noop_entry_missing");
        missingEntryReplay.ResultRevision.Should().Be(missingEntry.ResultRevision);
        emptyBlock.ResultStatus.Should().Be("noop_block_empty");
        emptyBlockReplay.ResultStatus.Should().Be("noop_block_empty");
        emptyBlockReplay.ResultRevision.Should().Be(emptyBlock.ResultRevision);
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
    public async Task PublicPlaylistReads_ShouldUseRevisionEtagsForAnonymousTokenlessReads()
    {
        await EnsurePostgresOrIgnore();
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        var owner = await SignIn(client);
        var playlist = await CreatePlaylist(client, owner.AccessToken);
        var firstTrack = await AddTrack(
            client,
            owner.AccessToken,
            playlist.PlaylistUuid,
            Guid.NewGuid(),
            Guid.NewGuid());
        var publicPlaylist = await UpdateVisibility(
            client,
            owner.AccessToken,
            playlist.PlaylistUuid,
            "public");

        publicPlaylist.Visibility.Should().Be("public");
        publicPlaylist.CurrentRevision.Should().Be(firstTrack.ResultRevision + 1);

        var anonymousRead = await client.GetAsync(
            $"/api/v3/library/playlists/{publicPlaylist.ShortId}?hydrate=false");
        var anonymousBody = await anonymousRead.Content.ReadAsStringAsync();
        var expectedEtag = $"\"playlist-{publicPlaylist.ShortId}-rev-{publicPlaylist.CurrentRevision}\"";

        anonymousRead.StatusCode.Should().Be(HttpStatusCode.OK, anonymousBody);
        anonymousRead.Headers.CacheControl!.Public.Should().BeTrue();
        anonymousRead.Headers.CacheControl.MaxAge.Should().Be(TimeSpan.FromSeconds(300));
        anonymousRead.Headers.CacheControl.NoStore.Should().BeFalse();
        anonymousRead.Headers.ETag!.Tag.Should().Be(expectedEtag);
        var vary = string.Join(", ", anonymousRead.Headers.Vary);
        vary.Should().Contain("Authorization");
        vary.Should().Contain("X-Relisten-Mobile-Grant");
        vary.Should().Contain("X-Relisten-Device-Id");
        anonymousRead.Headers.Pragma.Should().BeEmpty();
        var response = JsonConvert.DeserializeObject<PlaylistResponse>(
            anonymousBody,
            UserLibraryJson.SerializerSettings)!;
        response.Entries.Should().ContainSingle();

        var freshRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v3/library/playlists/{publicPlaylist.ShortId}");
        freshRequest.Headers.TryAddWithoutValidation("If-None-Match", expectedEtag);
        var notModified = await client.SendAsync(freshRequest);
        var notModifiedBody = await notModified.Content.ReadAsStringAsync();

        notModified.StatusCode.Should().Be(HttpStatusCode.NotModified, notModifiedBody);
        notModified.Headers.CacheControl!.Public.Should().BeTrue();
        notModified.Headers.ETag!.Tag.Should().Be(expectedEtag);
        notModifiedBody.Should().BeEmpty();

        var changed = await AddTrack(
            client,
            owner.AccessToken,
            playlist.PlaylistUuid,
            Guid.NewGuid(),
            Guid.NewGuid());
        var changedRead = await client.GetAsync($"/api/v3/library/playlists/{publicPlaylist.ShortId}");

        changedRead.StatusCode.Should().Be(HttpStatusCode.OK);
        changedRead.Headers.ETag!.Tag.Should()
            .Be($"\"playlist-{publicPlaylist.ShortId}-rev-{changed.ResultRevision}\"");
    }

    [Test]
    public async Task PublicPlaylistAuthenticatedReads_ShouldRemainNoStore()
    {
        await EnsurePostgresOrIgnore();
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        var owner = await SignIn(client);
        var playlist = await CreatePlaylist(client, owner.AccessToken);
        var publicPlaylist = await UpdateVisibility(
            client,
            owner.AccessToken,
            playlist.PlaylistUuid,
            "public");
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v3/library/playlists/{publicPlaylist.ShortId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", owner.AccessToken);

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        response.Headers.ETag.Should().BeNull();
    }

    [Test]
    public async Task PublicPlaylistPartialMobileGrantHeader_ShouldRemainNoStore()
    {
        await EnsurePostgresOrIgnore();
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        var owner = await SignIn(client);
        var playlist = await CreatePlaylist(client, owner.AccessToken);
        var publicPlaylist = await UpdateVisibility(
            client,
            owner.AccessToken,
            playlist.PlaylistUuid,
            "public");
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v3/library/playlists/{publicPlaylist.ShortId}");
        request.Headers.Add("X-Relisten-Mobile-Grant", "selector.secret");

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        response.Headers.ETag.Should().BeNull();

        var deviceOnlyRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v3/library/playlists/{publicPlaylist.ShortId}");
        deviceOnlyRequest.Headers.Add("X-Relisten-Device-Id", "ios-simulator");
        var deviceOnlyResponse = await client.SendAsync(deviceOnlyRequest);

        deviceOnlyResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        deviceOnlyResponse.Headers.CacheControl!.NoStore.Should().BeTrue();
        deviceOnlyResponse.Headers.ETag.Should().BeNull();
    }

    [Test]
    public async Task PublicToPrivateTransition_ShouldHideAnonymousReadsButRetainFollowerAccess()
    {
        await EnsurePostgresOrIgnore();
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        var owner = await SignIn(client);
        var follower = await SignIn(client);
        var playlist = await CreatePlaylist(client, owner.AccessToken);
        var publicPlaylist = await UpdateVisibility(
            client,
            owner.AccessToken,
            playlist.PlaylistUuid,
            "public");
        await FollowPlaylist(client, follower.AccessToken, playlist.PlaylistUuid);

        var privatePlaylist = await UpdateVisibility(
            client,
            owner.AccessToken,
            playlist.PlaylistUuid,
            "private");
        var anonymousRead = await client.GetAsync($"/api/v3/library/playlists/{publicPlaylist.ShortId}");
        var followerRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v3/library/playlists/{publicPlaylist.ShortId}");
        followerRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", follower.AccessToken);
        var followerRead = await client.SendAsync(followerRequest);

        privatePlaylist.Visibility.Should().Be("private");
        privatePlaylist.CurrentRevision.Should().Be(publicPlaylist.CurrentRevision + 1);
        anonymousRead.StatusCode.Should().Be(HttpStatusCode.NotFound);
        anonymousRead.Headers.CacheControl!.NoStore.Should().BeTrue();
        anonymousRead.Headers.ETag.Should().BeNull();
        followerRead.StatusCode.Should().Be(HttpStatusCode.OK);
        followerRead.Headers.CacheControl!.NoStore.Should().BeTrue();
        followerRead.Headers.ETag.Should().BeNull();
    }

    [Test]
    public async Task PrivateAnonymousRead_ShouldRemainNotFoundNoStore()
    {
        await EnsurePostgresOrIgnore();
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        var owner = await SignIn(client);
        var playlist = await CreatePlaylist(client, owner.AccessToken);

        var response = await client.GetAsync($"/api/v3/library/playlists/{playlist.ShortId}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        response.Headers.ETag.Should().BeNull();
    }

    [Test]
    public async Task HydratedPlaylistReads_ShouldUseExplicitUnsupportedBoundary()
    {
        await EnsurePostgresOrIgnore();
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        var owner = await SignIn(client);
        var playlist = await CreatePlaylist(client, owner.AccessToken);
        var publicPlaylist = await UpdateVisibility(
            client,
            owner.AccessToken,
            playlist.PlaylistUuid,
            "public");

        var response = await client.GetAsync($"/api/v3/library/playlists/{publicPlaylist.ShortId}?hydrate=true");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        JObject.Parse(body)["error"]!.Value<string>().Should().Be("hydration_not_supported");
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        response.Headers.ETag.Should().BeNull();
    }

    [Test]
    public async Task UpdateVisibility_ShouldBeOwnerOnlyAndValidateValues()
    {
        await EnsurePostgresOrIgnore();
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        var owner = await SignIn(client);
        var other = await SignIn(client);
        var playlist = await CreatePlaylist(client, owner.AccessToken);

        var nonOwner = await UpdateVisibilityResponse(
            client,
            other.AccessToken,
            playlist.PlaylistUuid,
            "public");
        var invalid = await UpdateVisibilityResponse(
            client,
            owner.AccessToken,
            playlist.PlaylistUuid,
            "discoverable");
        var invalidBody = await invalid.Content.ReadAsStringAsync();

        nonOwner.StatusCode.Should().Be(HttpStatusCode.NotFound);
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest, invalidBody);
        JObject.Parse(invalidBody)["error"]!.Value<string>().Should().Be("invalid_visibility");
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

    private static async Task<PlaylistResponse> UpdateVisibility(
        HttpClient client,
        string accessToken,
        Guid playlistUuid,
        string visibility)
    {
        var response = await UpdateVisibilityResponse(client, accessToken, playlistUuid, visibility);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonConvert.DeserializeObject<PlaylistResponse>(
            body,
            UserLibraryJson.SerializerSettings)!;
    }

    private static async Task<HttpResponseMessage> UpdateVisibilityResponse(
        HttpClient client,
        string accessToken,
        Guid playlistUuid,
        string visibility)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Patch,
            $"/api/v3/library/playlists/{playlistUuid}/visibility");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent(new UpdatePlaylistVisibilityRequest { Visibility = visibility });
        return await client.SendAsync(request);
    }

    private static async Task<PlaylistViewerStateResponse> FollowPlaylist(
        HttpClient client,
        string accessToken,
        Guid playlistUuid)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v3/library/playlists/{playlistUuid}/follow");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonConvert.DeserializeObject<PlaylistViewerStateResponse>(
            body,
            UserLibraryJson.SerializerSettings)!;
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

    private static Task<PlaylistOperationResponse> AddTrack(
        HttpClient client,
        string accessToken,
        Guid playlistUuid,
        Guid entryUuid,
        Guid sourceTrackUuid)
    {
        return ApplyOperation(
            client,
            accessToken,
            playlistUuid,
            new PlaylistOperationRequest
            {
                Op = "add_track",
                IdempotencyKey = Guid.NewGuid(),
                EntryUuid = entryUuid,
                SourceTrackUuid = sourceTrackUuid
            });
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
        await using var connection = NewDbService().CreateConnection();

        try
        {
            await connection.OpenAsync();
        }
        catch
        {
            Assert.Ignore("Local Postgres is not available for playlist integration tests.");
        }
    }

    private static async Task<CatalogSourceFixture> SeedCatalogSource(int trackCount)
    {
        await using var connection = NewDbService().CreateConnection();
        await connection.OpenAsync();
        var artistId = await connection.QuerySingleOrDefaultAsync<int?>(
            "SELECT id FROM public.artists ORDER BY id LIMIT 1");
        if (!artistId.HasValue)
        {
            Assert.Ignore("Local catalog seed data does not include an artist for source-range tests.");
        }

        var sourceUuid = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var sourceId = await connection.QuerySingleAsync<long>(
            """
            INSERT INTO public.sources
                (is_soundboard, is_remaster, avg_rating, num_reviews, upstream_identifier,
                 has_jamcharts, updated_at, artist_id, display_date, flac_type, uuid)
            VALUES
                (FALSE, FALSE, 0, 0, @UpstreamIdentifier, FALSE, @Now, @ArtistId,
                 @DisplayDate, 0, @SourceUuid)
            RETURNING id
            """,
            new
            {
                UpstreamIdentifier = $"playlist-test-source-{sourceUuid:N}",
                Now = now,
                ArtistId = artistId.Value,
                DisplayDate = $"playlist-test-{sourceUuid:N}",
                SourceUuid = sourceUuid
            });

        var trackUuids = new List<Guid>();
        for (var position = 1; position <= trackCount; position++)
        {
            var trackUuid = Guid.NewGuid();
            trackUuids.Add(trackUuid);
            await connection.ExecuteAsync(
                """
                INSERT INTO public.source_tracks
                    (source_id, track_position, duration, title, slug, mp3_url,
                     updated_at, artist_id, uuid)
                VALUES
                    (@SourceId, @TrackPosition, 180, @Title, @Slug, @Mp3Url,
                     @Now, @ArtistId, @TrackUuid)
                """,
                new
                {
                    SourceId = sourceId,
                    TrackPosition = position,
                    Title = $"Playlist Test Track {position}",
                    Slug = $"playlist-test-track-{trackUuid:N}",
                    Mp3Url = $"https://test.invalid/{trackUuid:N}.mp3",
                    Now = now,
                    ArtistId = artistId.Value,
                    TrackUuid = trackUuid
                });
        }

        return new CatalogSourceFixture(sourceUuid, trackUuids);
    }

    private static async Task<int> CountBlockRows(Guid blockUuid)
    {
        await using var connection = NewDbService().CreateConnection();
        return await connection.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM user_data.playlist_blocks WHERE id = @BlockUuid",
            new { BlockUuid = blockUuid });
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
        return $"playlist_{Guid.NewGuid():N}"[..30];
    }

    private static string DatabaseUrl =>
        Environment.GetEnvironmentVariable("DATABASE_URL") ??
        "postgresql://relisten:local_dev_password@127.0.0.1:15432/relisten_db";

    private const string TestSigningKey = "test-access-token-signing-key-with-more-than-32-bytes";

    private sealed record CatalogSourceFixture(Guid SourceUuid, IReadOnlyList<Guid> TrackUuids);
}
