using Dapper;
using FluentAssertions;
using Npgsql;
using Relisten.Identifiers;
using Relisten.Search;
using Relisten.Services.Popularity;

namespace RelistenApiTests.Search;

[TestFixture, Category("LocalIngestion")]
public sealed class TestCatalogSearchProjection
{
    [Test]
    public async Task ProjectsBothGardenCutsWithTheirOwnFileIdsAndExcludesUnavailableFiles()
    {
        MigratedTestDatabase.RequireLocalDatabase();
        await using var connection = await MigratedTestDatabase.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var show = Guid.NewGuid();
        var files = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        await SeedGardenShow(connection, transaction, show, files);

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
            .Performances(CancellationToken.None)
            .SingleAsync(document => (string)document["show_id"] == showId);
        document["label"].Should().Be("Lawn Boy");
        document["track_plays"].Should().Be(11L);
        document["review_mentions"].Should().Be(1);

        var cuts = (CatalogSearchProjection.SearchFile[])document["files"];
        cuts.Select(cut => (cut.TrackId, cut.Set, cut.Position, cut.Duration))
            .Should()
            .Equal(
                (UuidTypeId.Format("track", files[0]), "Set 1", 2, 1798),
                (UuidTypeId.Format("track", files[1]), "Encore", 14, 88)
            );
        var preview = (CatalogSearchProjection.SearchFile)document["preview"];
        preview.TrackId.Should().Be(UuidTypeId.Format("track", files[1]));

        var unlinkedVenue = await projection
            .Entities(CancellationToken.None)
            .SingleAsync(document => document["label"].Equals($"Unlinked venue {show}"));
        unlinkedVenue["popularity"].Should().Be(0d);
    }

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
                INSERT INTO shows(uuid, artist_id, venue_id, date, display_date, updated_at)
                SELECT @show, artist_id, id, '2017-07-25', '2017-07-25', now()
                FROM garden RETURNING id, artist_id
            ), recording AS (
                INSERT INTO sources(uuid, artist_id, show_id, upstream_identifier, display_date,
                                    is_soundboard, is_remaster, has_jamcharts, updated_at)
                SELECT gen_random_uuid(), artist_id, id, @show::text, '2017-07-25', true, false, false, now()
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
