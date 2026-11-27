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
        // The artist with more tracks comes first.
        listing
            .ArtistIds.Should()
            .Equal(new ArtistId(artist.Uuid).ToString(), new ArtistId(other.Uuid).ToString());
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
        edited.ArtistIds.Should().Equal(new ArtistId(artist.Uuid).ToString());

        // A metadata edit advances the indexed revision without rebuilding, and keeps the facts.
        listing.Description.Should().BeNull();
        await Apply(
            request.PlaylistId,
            new(
                1,
                [
                    Op(
                        "update_metadata",
                        new UpdatePlaylistMetadata(new(true, "Renamed"), new(true, "Some *jams*."))
                    ),
                ]
            )
        );
        var renamed = (await ListArtist(artist.Uuid)).Playlists.Single();
        renamed.Name.Should().Be("Renamed");
        renamed.Description.Should().Be("Some *jams*.");
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

        // Materialized days count; today's plays wait for the day to be materialized.
        await RecordPlaylistPlaysAsync(artist.Tracks[0], ids[1], plays: 3, daysAgo: 2);
        await RecordPlaylistPlaysAsync(artist.Tracks[0], ids[2], plays: 1, daysAgo: 1);
        await RecordPlaylistPlaysAsync(artist.Tracks[0], ids[0], plays: 5, daysAgo: 0);
        await using (var connection = new NpgsqlConnection(_database.ConnectionString))
        {
            await connection.OpenAsync();
            await Relisten.Services.Popularity.PopularityService.PublishPlaylistPopularity(
                connection,
                default
            );
        }
        var played = await ListArtist(artist.Uuid, sort: "plays");
        played.Playlists.Select(item => item.PlaylistId).Should().Equal(ids[1], ids[2], ids[0]);
        played
            .Playlists.Select(item => item.Popularity?.Windows.Days30.Plays ?? 0)
            .Should()
            .Equal(3, 1, 0);

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

    [Test]
    public async Task Concurrent_follows_keep_one_live_count_across_public_and_owner_reads()
    {
        var artist = await SeedArtistAsync(60);
        var request = PlaylistOf(artist.Tracks[0]);
        (await Send(HttpMethod.Post, "/v1/playlists", request))
            .StatusCode.Should()
            .Be(HttpStatusCode.Created);
        var code = (await Publish(request.PlaylistId)).PublicCode;
        var first = await NewSharingListener();
        var second = await NewSharingListener();
        var url = $"/v1/public-playlists/{code}";
        var follows = await Task.WhenAll(
            Send(HttpMethod.Put, $"{url}/follow", actor: first),
            Send(HttpMethod.Put, $"{url}/follow", actor: second)
        );
        follows
            .Select(row => row.StatusCode)
            .Should()
            .OnlyContain(status => status == HttpStatusCode.NoContent);
        (await Send(HttpMethod.Put, $"{url}/follow", actor: second))
            .StatusCode.Should()
            .Be(HttpStatusCode.NoContent);
        (await Read<PublicPlaylistSnapshot>(await _client.GetAsync(url)))
            .FollowerCount.Should()
            .Be(2);
        var list = await Read<PlaylistList>(await Send(HttpMethod.Get, "/v1/playlists"));
        list.Playlists.Single(row => row.PlaylistId == request.PlaylistId)
            .FollowerCount.Should()
            .Be(2);

        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.ExecuteAsync(
            "DELETE FROM identity.users WHERE id = @id",
            new { id = second.Value }
        );
        (await Read<PublicPlaylistSnapshot>(await _client.GetAsync(url)))
            .FollowerCount.Should()
            .Be(1);
    }

    [Test]
    public async Task Repeated_unfollow_and_deleting_another_playlist_preserve_the_count()
    {
        var artist = await SeedArtistAsync(60);
        var playlists = new[] { PlaylistOf(artist.Tracks[0]), PlaylistOf(artist.Tracks[0]) };
        var codes = new List<string?>();
        var listener = await NewSharingListener();
        foreach (var playlist in playlists)
        {
            (await Send(HttpMethod.Post, "/v1/playlists", playlist))
                .StatusCode.Should()
                .Be(HttpStatusCode.Created);
            var code = (await Publish(playlist.PlaylistId)).PublicCode;
            codes.Add(code);
            (await Send(HttpMethod.Put, $"/v1/public-playlists/{code}/follow", actor: listener))
                .StatusCode.Should()
                .Be(HttpStatusCode.NoContent);
        }

        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.ExecuteAsync(
            "DELETE FROM user_data.playlists WHERE id = @id",
            new { id = playlists[0].PlaylistId.Value }
        );
        (await _client.GetAsync($"/v1/public-playlists/{codes[0]}"))
            .StatusCode.Should()
            .Be(HttpStatusCode.NotFound);
        var surviving = $"/v1/public-playlists/{codes[1]}";
        (await Read<PublicPlaylistSnapshot>(await _client.GetAsync(surviving)))
            .FollowerCount.Should()
            .Be(1);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            (
                await Send(
                    HttpMethod.Delete,
                    $"/v1/playlists/{playlists[1].PlaylistId}/follow",
                    actor: listener
                )
            )
                .StatusCode.Should()
                .Be(HttpStatusCode.NoContent);
            (await Read<PublicPlaylistSnapshot>(await _client.GetAsync(surviving)))
                .FollowerCount.Should()
                .Be(0);
        }
    }

    [Test]
    public async Task Daily_momentum_favors_the_playlist_with_more_plays_on_the_latest_complete_day()
    {
        var artist = await SeedArtistAsync(60);
        var rising = PlaylistOf(artist.Tracks[0]);
        var fading = PlaylistOf(artist.Tracks[0]);
        foreach (var playlist in new[] { rising, fading })
        {
            (await Send(HttpMethod.Post, "/v1/playlists", playlist))
                .StatusCode.Should()
                .Be(HttpStatusCode.Created);
            await Publish(playlist.PlaylistId);
        }
        await RecordPlaylistPlaysAsync(artist.Tracks[0], rising.PlaylistId, 10, daysAgo: 2);
        await RecordPlaylistPlaysAsync(artist.Tracks[0], fading.PlaylistId, 20, daysAgo: 2);
        await RecordPlaylistPlaysAsync(artist.Tracks[0], rising.PlaylistId, 20, daysAgo: 1);
        await RecordPlaylistPlaysAsync(artist.Tracks[0], fading.PlaylistId, 10, daysAgo: 1);
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await Relisten.Services.Popularity.PopularityService.PublishPlaylistPopularity(
            connection,
            default
        );
        var cards = (await ListArtist(artist.Uuid)).Playlists.ToDictionary(
            row => row.PlaylistId,
            row => row.Popularity!
        );
        cards
            .Values.Select(view => view.Windows.Year!.Plays)
            .Should()
            .OnlyContain(plays => plays == 30);
        cards[rising.PlaylistId]
            .MomentumScore.Should()
            .BeGreaterThan(cards[fading.PlaylistId].MomentumScore);
    }

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

    /// <summary>
    /// Records plays from a playlist at noon UTC, <paramref name="daysAgo"/> days back.
    /// A closed day is materialized; today's plays stay in the real-time tail.
    /// </summary>
    private async Task RecordPlaylistPlaysAsync(
        string track,
        PlaylistId playlist,
        int plays,
        int daysAgo
    )
    {
        var day = DateTime.UtcNow.Date.AddDays(-daysAgo);
        var playedAt = daysAgo == 0 ? DateTime.UtcNow.AddMinutes(-1) : day.AddHours(12);
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
        if (daysAgo > 0)
        {
            await connection.ExecuteAsync(
                "CALL refresh_continuous_aggregate('public.source_track_plays_playlist_daily', @start, @end)",
                new { start = day, end = day.AddDays(1) }
            );
        }
    }
}
