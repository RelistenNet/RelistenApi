using System.Text.Json;
using Dapper;
using FluentAssertions;
using Npgsql;
using Relisten.Identifiers;
using Relisten.Search;
using Relisten.Services.Popularity;
using RelistenApiTests.V4;

namespace RelistenApiTests.Search;

[TestFixture, Category("LocalIngestion")]
public sealed class TestCatalogSearchProjection
{
    [Test]
    public async Task PercentilesMatchThePopulationsOfTheEmittedDocuments()
    {
        MigratedTestDatabase.RequireLocalDatabase();
        await using var connection = await MigratedTestDatabase.OpenAsync();
        await V4CatalogFixture.EnsureLoaded(connection);
        await using var transaction = await connection.BeginTransactionAsync();
        var show = Guid.NewGuid();
        var files = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        await SeedGardenShow(connection, transaction, show, files);
        var medley = Guid.Parse("00000000-0000-0000-0000-000000000044");
        var projection = await CatalogSearchProjection.Open(
            new SearchProjectionRows(connection, transaction),
            new Dictionary<Guid, TrackStartTotal>
            {
                [medley] = new(medley, 12),
                [files[0]] = new(files[0], 4),
                [files[1]] = new(files[1], 7),
            },
            CancellationToken.None
        );
        foreach (var (_, stream) in projection.Documents(CancellationToken.None).InBuildOrder())
        {
            var documents = await stream.ToArrayAsync();
            var distribution = new SearchPercentiles();
            foreach (var row in documents)
            {
                distribution.Observe(
                    (string)row["kind"],
                    (float)Convert.ToDouble(row["ranking_signal"])
                );
            }
            var ranks = distribution.Calculate();
            foreach (var row in documents)
            {
                Convert
                    .ToDouble(row.GetValueOrDefault("popularity_percentile"))
                    .Should()
                    .Be(ranks[(string)row["kind"]][(float)Convert.ToDouble(row["ranking_signal"])]);
            }
        }
    }

    [Test]
    public async Task CalibratedPerformancesRankGroupedFilesAndKeepUnplayedSongsAtZero()
    {
        MigratedTestDatabase.RequireLocalDatabase();
        await using var connection = await MigratedTestDatabase.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var show = Guid.NewGuid();
        var files = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        var extras = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray();
        await SeedGardenShow(connection, transaction, show, files);
        await connection.ExecuteAsync(
            """
            INSERT INTO source_tracks(uuid,artist_id,source_id,source_set_id,track_position,
                                      title,slug,mp3_url,duration,updated_at)
            SELECT file.uuid,src.artist_id,src.id,set.id,file.position + 20,file.title,file.position::text,
                   'https://example.test/' || file.uuid || '.mp3',60,now()
            FROM shows sh JOIN sources src ON src.show_id=sh.id
            JOIN source_sets set ON set.source_id=src.id AND set.index=1
            CROSS JOIN unnest(@extras::uuid[],ARRAY['(soundcheck) Version A','Version B','No starts'])
                WITH ORDINALITY AS file(uuid,title,position)
            WHERE sh.uuid=@show
            """,
            new { show, extras },
            transaction
        );
        var artist = await connection.QuerySingleAsync<int>(
            "SELECT artist_id FROM shows WHERE uuid=@show",
            new { show },
            transaction
        );
        var plays = new Dictionary<Guid, TrackStartTotal>
        {
            [files[0]] = new(files[0], 4),
            [files[1]] = new(files[1], 7),
            [files[2]] = new(files[2], 1000),
            [extras[0]] = new(extras[0], 5),
            [extras[1]] = new(extras[1], 5),
        };
        var projection = await CatalogSearchProjection.Open(
            new SearchProjectionRows(connection, transaction, artist),
            plays,
            CancellationToken.None
        );
        var documents = projection.Documents(CancellationToken.None);
        var performances = await documents.Performances.ToDictionaryAsync(row =>
            (string)row["label"]
        );

        var soundcheck = performances["(soundcheck) Version A"];
        soundcheck["soundcheck"].Should().Be(true);
        var tied = (double)soundcheck["popularity_percentile"];
        tied.Should().BePositive();
        performances["Version B"]["popularity_percentile"].Should().Be(tied);
        var lawnBoy = (double)performances["Lawn Boy"]["popularity_percentile"];
        lawnBoy.Should().BeGreaterThan(tied);
        performances["No starts"].Should().NotContainKey("popularity_percentile");

        // The only recording is higher in raw starts than any one song, but
        // has its own population and therefore a lower percentile.
        var recording = await documents.Recordings.SingleAsync();
        lawnBoy.Should().BeGreaterThan((double)recording["popularity_percentile"]);
    }

    [Test]
    public async Task PreservesPerformanceTotalsAndProjectsTheExactPlayablePreview()
    {
        MigratedTestDatabase.RequireLocalDatabase();
        await using var connection = await MigratedTestDatabase.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var show = Guid.NewGuid();
        var files = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        await SeedGardenShow(connection, transaction, show, files);
        await connection.ExecuteAsync(
            """
            INSERT INTO show_popularity(uuid,artist_uuid,is_global,plays_30d,hours_30d,hot_score_30d,
                plays_7d,hours_7d,hot_score_7d,plays_48h,hours_48h,hot_score_48h,trend_ratio,
                momentum_score,generated_at)
            SELECT s.uuid,a.uuid,false,101,12,10,8,2,5,1,0.25,2,1.25,42,now()
            FROM shows s JOIN artists a ON a.id=s.artist_id WHERE s.uuid=@show;
            INSERT INTO song_performance_popularity(uuid,artist_uuid,song_id,plays_30d,hours_30d,hot_score_30d,
                plays_7d,hours_7d,hot_score_7d,plays_48h,hours_48h,hot_score_48h,trend_ratio,
                generated_at,plays_1y,hours_1y,hot_score_1y)
            SELECT s.uuid,a.uuid,@performance,0,0,0,0,0,0,0,0,0,0,now(),61,1.5,2
            FROM shows s JOIN artists a ON a.id=s.artist_id WHERE s.uuid=@show;
            INSERT INTO recording_popularity(uuid,artist_uuid,plays_30d,hours_30d,hot_score_30d,
                plays_7d,hours_7d,hot_score_7d,plays_48h,hours_48h,hot_score_48h,trend_ratio,
                generated_at,plays_1y,hours_1y,hot_score_1y)
            SELECT src.uuid,a.uuid,0,0,0,0,0,0,0,0,0,0,now(),83,3,3
            FROM sources src JOIN shows s ON s.id=src.show_id JOIN artists a ON a.id=src.artist_id
            WHERE s.uuid=@show
            """,
            new
            {
                show,
                performance = PopularityPerformanceKey.ForStorage(
                    PopularityPerformanceKey.RawTitle("lawn boy")
                ),
            },
            transaction
        );

        var plays = new Dictionary<Guid, TrackStartTotal>
        {
            [files[0]] = new(files[0], 4),
            [files[1]] = new(files[1], 7),
            [files[2]] = new(files[2], 1_000),
            [files[3]] = new(files[3], 2_000),
        };
        var projection = await CatalogSearchProjection.Open(
            new SearchProjectionRows(connection, transaction),
            plays,
            CancellationToken.None
        );

        var showId = UuidTypeId.Format("show", show);
        var review = await projection
            .Reviews(CancellationToken.None)
            .SingleAsync(document => (string)document["show_id"] == showId);
        review["review_text"].Should().Be("Favorite Lawn Boy lawn boy was the highlight");

        var document = await projection
            .Performances(CancellationToken.None, calibrate: true)
            .SingleAsync(document => (string)document["show_id"] == showId);
        document["label"].Should().Be("Lawn Boy");
        document["track_plays"].Should().Be(61L);
        ((double)document["popularity_percentile"]).Should().BePositive();
        document["review_mentions"].Should().Be(1);
        YearlyPlays(document).Should().Be(61);

        document.Should().NotContainKey("files");
        document.Should().NotContainKey("display_song_id");
        var preview = (JsonElement)document["display"];
        preview.GetProperty("id").GetString().Should().Be(UuidTypeId.Format("track", files[1]));
        preview.GetProperty("slug").GetString().Should().Be("2");
        preview.GetProperty("playable").GetBoolean().Should().BeTrue();
        preview.GetProperty("flac_url").GetString().Should().Be("https://example.test/encore.flac");

        var showDocument = await projection
            .Entities(CancellationToken.None)
            .SingleAsync(document => document["id"].Equals(showId));
        showDocument["track_plays"].Should().Be(11L);
        showDocument["avg_rating"].Should().Be(8.125f);
        showDocument["location_label"].Should().Be("New York, NY");
        var embeddedPopularity = ((JsonElement)showDocument["display"]).GetProperty("popularity");
        embeddedPopularity.GetProperty("momentum_score").GetDouble().Should().Be(42);
        embeddedPopularity
            .GetProperty("windows")
            .GetProperty("30d")
            .GetProperty("plays")
            .GetInt64()
            .Should()
            .Be(101);
        embeddedPopularity.GetProperty("windows").TryGetProperty("1y", out _).Should().BeFalse();

        var recording = await projection
            .Recordings(CancellationToken.None)
            .SingleAsync(document => (string)document["show_id"] == showId);
        recording["track_plays"].Should().Be(11L);
        YearlyPlays(recording).Should().Be(83);
        recording["avg_rating"].Should().Be(8.5f);
        recording["num_reviews"].Should().Be(1);
        recording["taper_label"].Should().Be("Charlie Miller");
        recording["transferrer_label"].Should().Be("Kevin Tobin");

        var unlinkedVenue = await projection
            .Entities(CancellationToken.None)
            .SingleAsync(document => document["label"].Equals($"Unlinked venue {show}"));
        unlinkedVenue["popularity"].Should().Be(0d);
    }

    [TestCase("2017-07-25", true)]
    [TestCase("2017-07-26", false)]
    public async Task RawTitleNeedsSameShowMembershipToUseImportedSongId(
        string memberDate,
        bool identifiesSong
    )
    {
        MigratedTestDatabase.RequireLocalDatabase();
        await using var connection = await MigratedTestDatabase.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var show = Guid.NewGuid();
        var files = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        await SeedGardenShow(connection, transaction, show, files);

        var song = Guid.NewGuid();
        await connection.ExecuteAsync(
            """
            WITH imported_show AS (
                INSERT INTO setlist_shows(uuid,artist_id,venue_id,upstream_identifier,date,updated_at)
                SELECT gen_random_uuid(),artist_id,venue_id,@show::text,@memberDate::date,now()
                FROM shows WHERE uuid=@show
                RETURNING id,artist_id
            ), imported_song AS (
                INSERT INTO setlist_songs(uuid,artist_id,name,slug,upstream_identifier,updated_at)
                SELECT @song,artist_id,'Lawn Boy','lawn-boy',@song::text,now() FROM imported_show
                RETURNING id
            )
            INSERT INTO setlist_songs_plays(played_setlist_song_id,played_setlist_show_id)
            SELECT imported_song.id,imported_show.id FROM imported_song CROSS JOIN imported_show
            """,
            new
            {
                show,
                song,
                memberDate,
            },
            transaction
        );

        var projection = await CatalogSearchProjection.Open(
            new SearchProjectionRows(connection, transaction),
            [],
            CancellationToken.None
        );
        var showId = UuidTypeId.Format("show", show);
        var performance = await projection
            .Performances(CancellationToken.None)
            .SingleAsync(document => document["show_id"].Equals(showId));

        if (identifiesSong)
        {
            performance["display_song_id"].Should().Be(UuidTypeId.Format("imported_song", song));
        }
        else
        {
            performance.Should().NotContainKey("display_song_id");
        }
        ((JsonElement)performance["display"])
            .GetProperty("playable")
            .GetBoolean()
            .Should()
            .BeTrue();
    }

    private static long YearlyPlays(IReadOnlyDictionary<string, object> document) =>
        ((JsonElement)document["display_popularity"])
            .GetProperty("windows")
            .GetProperty("1y")
            .GetProperty("plays")
            .GetInt64();

    private static Task SeedGardenShow(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid show,
        Guid[] files
    ) =>
        connection.ExecuteAsync(
            """
            WITH artist AS (
                INSERT INTO artists(uuid, name, slug, updated_at)
                VALUES (gen_random_uuid(), 'Phish fixture', @show::text, now()) RETURNING id
            ), garden AS (
                INSERT INTO venues(uuid, artist_id, name, slug, location, updated_at)
                SELECT gen_random_uuid(), id, 'Madison Square Garden', 'garden', 'New York, NY', now()
                FROM artist RETURNING id, artist_id
            ), unlinked_venue AS (
                INSERT INTO venues(uuid, artist_id, name, slug, location, updated_at)
                SELECT gen_random_uuid(), id, 'Unlinked venue ' || @show::text, 'unlinked', 'New York, NY', now()
                FROM artist
            ), garden_show AS (
                INSERT INTO shows(uuid, artist_id, venue_id, date, display_date, avg_rating, updated_at)
                SELECT @show, artist_id, id, '2017-07-25', '2017-07-25', 8.125, now()
                FROM garden RETURNING id, artist_id
            ), recording AS (
                INSERT INTO sources(uuid, artist_id, show_id, upstream_identifier, display_date,
                                    is_soundboard, is_remaster, has_jamcharts, avg_rating, num_reviews,
                                    taper, transferrer, updated_at)
                SELECT gen_random_uuid(), artist_id, id, @show::text, '2017-07-25', true, false, false,
                       8.5, 1, 'Charlie Miller', 'Kevin Tobin', now()
                FROM garden_show RETURNING id, artist_id
            ), sets AS (
                INSERT INTO source_sets(uuid, source_id, index, name, updated_at)
                SELECT gen_random_uuid(), id, n, name, now()
                FROM recording CROSS JOIN (VALUES (1, 'Set 1'), (2, 'Encore')) AS names(n, name)
                RETURNING id, source_id, index
            ), review AS (
                INSERT INTO source_reviews(uuid, source_id, title, review, updated_at)
                SELECT gen_random_uuid(), id, 'Favorite Lawn Boy', 'lawn boy was the highlight', now()
                FROM recording
            )
            INSERT INTO source_tracks(uuid, artist_id, source_id, source_set_id, track_position,
                                      title, slug, mp3_url, flac_url, duration, is_orphaned, updated_at)
            SELECT (@files::uuid[])[n], r.artist_id, r.id, s.id, t.position, 'Lawn Boy', n::text,
                   t.mp3, t.flac, t.duration, t.orphaned, now()
            FROM recording r JOIN sets s ON s.source_id = r.id
            JOIN (VALUES (1, 1, 2, 'https://example.test/set.mp3', NULL, 1798, false),
                         (2, 2, 14, NULL, 'https://example.test/encore.flac', 88, false),
                         (3, 1, 3, 'https://example.test/orphan.mp3', NULL, 205, true),
                         (4, 2, 4, '', NULL, 301, false))
                 AS t(n, set_index, position, mp3, flac, duration, orphaned) ON s.index = t.set_index
            """,
            new { show, files },
            transaction
        );
}
