using System.Net;
using Dapper;
using FluentAssertions;
using Npgsql;
using NUnit.Framework;
using Relisten.Accounts.Contracts.Identifiers;
using Relisten.Accounts.Contracts.Playlists;
using Relisten.Identifiers;
using RelistenUserService.Playlists;

namespace RelistenUserServiceTests;

public sealed partial class TestPlaylistApi
{
    [Test]
    public async Task Artist_listing_follows_entry_edits_and_hides_private_playlists()
    {
        var artist = await SeedArtistAsync(300, 200);
        var other = await SeedArtistAsync(100);
        var unknownTrack = new TrackId(Guid.NewGuid()).ToString();
        var request = PlaylistOf(artist.Tracks[0], artist.Tracks[1], other.Tracks[0], unknownTrack);
        var created = await Send(HttpMethod.Post, "/v1/playlists", request);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var hidden = PlaylistOf(artist.Tracks[0]);
        (await Send(HttpMethod.Post, "/v1/playlists", hidden))
            .StatusCode.Should()
            .Be(HttpStatusCode.Created);

        (await ListArtist(artist.Uuid)).Playlists.Should().BeEmpty();
        var publication = await Publish(request.PlaylistId);

        using var anonymous = await _client.GetAsync(
            $"/v1/public-playlists?artist={new ArtistId(artist.Uuid)}"
        );
        anonymous.Headers.GetValues("CDN-Cache-Control").Should().Equal("max-age=60");
        var listing = (await Read<PublicPlaylistPage>(anonymous)).Playlists.Single();
        listing.PlaylistId.Should().Be(request.PlaylistId);
        listing.PublicCode.Should().Be(publication.PublicCode);
        listing.Name.Should().Be("Discovery playlist");
        listing.Owner.Id.Should().Be(_user);
        listing.TrackCount.Should().Be(4);
        // The unknown track counts as a track but adds no catalog duration or artist.
        listing.DurationSeconds.Should().Be(600);
        (await ListArtist(other.Uuid)).Playlists.Should().ContainSingle();

        var otherEntry = request.Segments[0].Entries[2].EntryId;
        await Apply(
            request.PlaylistId,
            new(1, [Op("delete_entry", new DeletePlaylistEntry(otherEntry))])
        );

        (await ListArtist(other.Uuid)).Playlists.Should().BeEmpty();
        var edited = (await ListArtist(artist.Uuid)).Playlists.Single();
        edited.DurationSeconds.Should().Be(500);
        edited.TrackCount.Should().Be(3);

        // A rename advances the indexed revision without rebuilding, and keeps the facts.
        await Apply(
            request.PlaylistId,
            new(1, [Op("update_metadata", new UpdatePlaylistMetadata(new(true, "Renamed")))])
        );
        var renamed = (await ListArtist(artist.Uuid)).Playlists.Single();
        renamed.Name.Should().Be("Renamed");
        renamed.DurationSeconds.Should().Be(500);
        await using (var connection = new NpgsqlConnection(_database.ConnectionString))
        {
            (
                await connection.QuerySingleAsync<bool>(
                    "SELECT catalog_indexed_revision = revision FROM user_data.playlists WHERE id = @id",
                    new { id = request.PlaylistId.Value }
                )
            )
                .Should()
                .BeTrue();
        }

        await Moderate("take-down", request.PlaylistId.Value);
        (await ListArtist(artist.Uuid)).Playlists.Should().BeEmpty();
    }

    [Test]
    public async Task Listing_pages_follow_each_sort_with_disjoint_cursors()
    {
        var artist = await SeedArtistAsync(60);
        var ids = new List<PlaylistId>();
        foreach (var _ in Enumerable.Range(0, 3))
        {
            var request = PlaylistOf(artist.Tracks[0]);
            (await Send(HttpMethod.Post, "/v1/playlists", request))
                .StatusCode.Should()
                .Be(HttpStatusCode.Created);
            await Publish(request.PlaylistId);
            ids.Add(request.PlaylistId);
        }

        // Publication moves a playlist to the front, so the last published is newest.
        var first = await ListArtist(artist.Uuid, limit: 2);
        first.Playlists.Select(item => item.PlaylistId).Should().Equal(ids[2], ids[1]);
        first.NextCursor.Should().NotBeNull();
        var second = await ListArtist(artist.Uuid, limit: 2, cursor: first.NextCursor);
        second.Playlists.Select(item => item.PlaylistId).Should().Equal(ids[0]);
        second.NextCursor.Should().BeNull();

        var follower = await NewSharingListener();
        var code = (await Publish(ids[0])).PublicCode;
        (await Send(HttpMethod.Put, $"/v1/public-playlists/{code}/follow", actor: follower))
            .StatusCode.Should()
            .Be(HttpStatusCode.NoContent);
        var followed = await ListArtist(artist.Uuid, sort: "followers", limit: 1);
        followed.Playlists.Single().PlaylistId.Should().Be(ids[0]);
        followed.Playlists.Single().FollowerCount.Should().Be(1);

        await RecordPlaylistPlaysAsync(artist.Tracks[0], ids[1], plays: 3);
        await RecordPlaylistPlaysAsync(artist.Tracks[0], ids[2], plays: 1);
        var played = await ListArtist(artist.Uuid, sort: "plays");
        played.Playlists.Select(item => item.PlaylistId).Should().Equal(ids[1], ids[2], ids[0]);
        played.Playlists.Select(item => item.RecentPlayCount).Should().Equal(3, 1, 0);

        await Problem(
            await _client.GetAsync(
                $"/v1/public-playlists?sort=plays&cursor={Uri.EscapeDataString(first.NextCursor!)}"
            ),
            422,
            "invalid_query"
        );
        await Problem(
            await _client.GetAsync("/v1/public-playlists?artist=not-an-artist"),
            422,
            "invalid_query"
        );
        await Problem(
            await _client.GetAsync("/v1/public-playlists?limit=51"),
            422,
            "invalid_query"
        );
    }

    [Test]
    public async Task Sweep_rebuilds_facts_that_lag_the_playlist_revision()
    {
        var artist = await SeedArtistAsync(120);
        var request = PlaylistOf(artist.Tracks[0]);
        (await Send(HttpMethod.Post, "/v1/playlists", request))
            .StatusCode.Should()
            .Be(HttpStatusCode.Created);
        await Publish(request.PlaylistId);
        await using (var connection = new NpgsqlConnection(_database.ConnectionString))
        {
            // The state an older build leaves after an edit, or a playlist from before the index.
            await connection.ExecuteAsync(
                """
                DELETE FROM user_data.playlist_artists WHERE playlist_id = @id;
                UPDATE user_data.playlists
                SET catalog_indexed_revision = 0, catalog_duration_seconds = 0
                WHERE id = @id;
                """,
                new { id = request.PlaylistId.Value }
            );
        }
        (await ListArtist(artist.Uuid)).Playlists.Should().BeEmpty();

        await using var database = _database.CreateContext();
        (await PlaylistCatalogIndex.RepairAsync(database, request.PlaylistId, default))
            .Should()
            .BeTrue();
        (await PlaylistCatalogIndex.RepairAsync(database, request.PlaylistId, default))
            .Should()
            .BeFalse();

        (await ListArtist(artist.Uuid)).Playlists.Single().DurationSeconds.Should().Be(120);
    }

    private static CreatePlaylistRequest PlaylistOf(params string[] tracks) =>
        new(
            1,
            CommandId.New(),
            PlaylistId.New(),
            new("Discovery playlist"),
            [
                new(
                    PlaylistSegmentId.New(),
                    tracks
                        .Select(track => new PlaylistEntryInput(PlaylistEntryId.New(), track))
                        .ToArray()
                ),
            ]
        );

    private async Task<PublicPlaylistPage> ListArtist(
        Guid artist,
        string sort = "recent",
        int limit = 25,
        string? cursor = null
    )
    {
        var url = $"/v1/public-playlists?artist={artist}&sort={sort}&limit={limit}";
        if (cursor is not null)
        {
            url += $"&cursor={Uri.EscapeDataString(cursor)}";
        }
        return await Read<PublicPlaylistPage>(await _client.GetAsync(url));
    }

    /// <summary>Adds an artist with one catalog track per duration, in seconds.</summary>
    private async Task<(Guid Uuid, string[] Tracks)> SeedArtistAsync(params int[] durations)
    {
        var artist = Guid.NewGuid();
        var tracks = durations.Select(_ => Guid.NewGuid()).ToArray();
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        var artistId = await connection.ExecuteScalarAsync<int>(
            """
            INSERT INTO public.artists (uuid, name, slug, updated_at)
            VALUES (@artist, 'Discovery artist', @slug, now())
            RETURNING id
            """,
            new { artist, slug = $"discovery-{artist:N}" }
        );
        for (var index = 0; index < tracks.Length; index++)
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO public.source_tracks (uuid, artist_id, track_position, title, slug,
                                                  mp3_url, duration, updated_at)
                VALUES (@track, @artistId, 1, 'Track', 'track', @url, @duration, now())
                """,
                new
                {
                    track = tracks[index],
                    artistId,
                    url = $"https://example.test/{tracks[index]:N}.mp3",
                    duration = durations[index],
                }
            );
        }
        return (artist, tracks.Select(track => new TrackId(track).ToString()).ToArray());
    }

    /// <summary>Records plays from a playlist in a closed hour and materializes them.</summary>
    private async Task RecordPlaylistPlaysAsync(string track, PlaylistId playlist, int plays)
    {
        var playedAt = DateTime.UtcNow.AddHours(-3);
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.ExecuteAsync(
            """
            INSERT INTO public.source_track_plays (source_track_uuid, app_type, created_at, playlist_id)
            SELECT @track, 2, @playedAt, @playlist FROM generate_series(1, @plays)
            """,
            new
            {
                track = TrackId.Parse(track).Value,
                playedAt,
                playlist = playlist.Value,
                plays,
            }
        );
        await connection.ExecuteAsync(
            "CALL refresh_continuous_aggregate('public.source_track_plays_playlist_hourly', @start, @end)",
            new { start = playedAt.AddHours(-1), end = playedAt.AddHours(1) }
        );
    }
}
