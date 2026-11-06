using Dapper;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Relisten;
using Relisten.Catalog;

namespace RelistenApiTests.V4;

public sealed partial class TestV4CatalogHttp
{
    [TestCase(CatalogSort.Default)]
    [TestCase(CatalogSort.DateDescending)]
    [TestCase(CatalogSort.Rating)]
    [TestCase(CatalogSort.Popularity)]
    public async Task Song_show_pages_cross_nonmatching_batches_without_skips(CatalogSort sort)
    {
        await WithShowScanFixture(async _ =>
        {
            var reader = app.Services.GetRequiredService<CatalogCollectionReader>();
            await using var session = await CatalogReadSession.Open(
                app.Services.GetRequiredService<DbService>(),
                CancellationToken.None
            );
            var collection = new CatalogCollection(
                CatalogReferenceKind.Show,
                CatalogReferenceKind.Song,
                "song_00000101",
                Sort: sort
            );
            var complete = await reader.ReadAll(session, collection, CancellationToken.None);
            complete.Targets.Should().HaveCount(7);
            var actual = new List<CatalogResolvedTarget>();
            CatalogPageKey? after = null;
            while (true)
            {
                var page = (
                    await reader.ReadPages(
                        session,
                        [(collection, new CatalogPageRequest(2, after))],
                        CancellationToken.None
                    )
                )[0];
                actual.AddRange(page.Targets);
                page.Keys.Should()
                    .Equal(complete.Keys.Skip(actual.Count - page.Targets.Count).Take(2));
                page.HasNextPage.Should().Be(actual.Count < complete.Targets.Count);
                if (!page.HasNextPage)
                {
                    break;
                }
                after = page.Keys[^1];
                actual.Count.Should().BeLessThanOrEqualTo(complete.Targets.Count);
            }
            actual.Should().Equal(complete.Targets);
            var exhausted = (
                await reader.ReadPages(
                    session,
                    [(collection, new CatalogPageRequest(2, complete.Keys[^1]))],
                    CancellationToken.None
                )
            )[0];
            exhausted.Targets.Should().BeEmpty();
            exhausted.HasNextPage.Should().BeFalse();
        });
    }

    [Test]
    public async Task Batched_song_show_pages_keep_filters_counts_and_sparse_fallback_independent()
    {
        await WithShowScanFixture(async writer =>
        {
            var reader = app.Services.GetRequiredService<CatalogCollectionReader>();
            await using var session = await CatalogReadSession.Open(
                app.Services.GetRequiredService<DbService>(),
                CancellationToken.None
            );
            var song = new CatalogCollection(
                CatalogReferenceKind.Show,
                CatalogReferenceKind.Song,
                "song_00000101",
                Sort: CatalogSort.DateDescending
            );
            CatalogCollection[] collections =
            [
                song,
                song with
                {
                    Filters = new(Song: "song_00000102"),
                },
                song with
                {
                    Owner = "song_00000102",
                },
                song with
                {
                    Owner = "song_00000103",
                },
                song with
                {
                    Filters = new(From: new DateTime(2020, 4, 1)),
                },
                new(
                    CatalogReferenceKind.Show,
                    CatalogReferenceKind.Artist,
                    CatalogId("artist", 301),
                    new(Song: "song_00000101"),
                    CatalogSort.DateDescending
                ),
                new(
                    CatalogReferenceKind.Show,
                    Filters: new(Song: "song_00000101"),
                    Sort: CatalogSort.DateDescending
                ),
                new(
                    CatalogReferenceKind.Show,
                    CatalogReferenceKind.Artist,
                    CatalogId("artist", 302),
                    new(Song: "song_00000101"),
                    CatalogSort.DateDescending
                ),
                song,
                song,
            ];
            var requests = collections
                .Select(
                    (c, i) =>
                        (
                            Collection: c,
                            Page: new CatalogPageRequest(
                                i == collections.Length - 2 ? 5 : 2,
                                Count: i == collections.Length - 1
                            )
                        )
                )
                .ToArray();
            var pages = await reader.ReadPages(session, requests, CancellationToken.None);
            for (var i = 0; i < collections.Length; i++)
            {
                var complete = await reader.ReadAll(
                    session,
                    collections[i],
                    CancellationToken.None
                );
                pages[i].Targets.Should().Equal(complete.Targets.Take(requests[i].Page.First));
                pages[i].Keys.Should().Equal(complete.Keys.Take(requests[i].Page.First));
                pages[i].HasNextPage.Should().Be(complete.Targets.Count > requests[i].Page.First);
                if (i == collections.Length - 1)
                {
                    pages[i].TotalCount.Should().Be(complete.Targets.Count);
                }
            }

            // Underestimated or absent counters choose complete matching, never an empty page.
            await writer.ExecuteAsync("DELETE FROM norm.song_counts WHERE song_id='song_00000101'");
            await using var next = await CatalogReadSession.Open(
                app.Services.GetRequiredService<DbService>(),
                CancellationToken.None
            );
            var withoutCount = (
                await reader.ReadPages(next, [(song, new(2))], CancellationToken.None)
            )[0];
            withoutCount.Should().BeEquivalentTo(pages[0], options => options.WithStrictOrdering());
        });
    }

    [Test]
    public async Task Show_scans_keep_the_callers_snapshot_after_recording_membership_changes()
    {
        await WithShowScanFixture(async writer =>
        {
            var reader = app.Services.GetRequiredService<CatalogCollectionReader>();
            var db = app.Services.GetRequiredService<DbService>();
            var collection = new CatalogCollection(
                CatalogReferenceKind.Show,
                CatalogReferenceKind.Song,
                "song_00000101",
                Sort: CatalogSort.DateDescending
            );
            await using var snapshot = await CatalogReadSession.Open(db, CancellationToken.None);
            var original = await reader.ReadAll(snapshot, collection, CancellationToken.None);
            await writer.ExecuteAsync(
                """
                SET norm.author_type='importer'; SET norm.author_ref='v4-review';
                SET norm.run_uuid='00000000-0000-0000-0000-000000000099';
                UPDATE norm.track_songs SET resolved=false
                WHERE track_uuid='20000000-0000-0000-0003-000000000145';
                """
            );
            var same = (
                await reader.ReadPages(snapshot, [(collection, new(2))], CancellationToken.None)
            )[0];
            same.Targets.Should().Equal(original.Targets.Take(2));
            await using var next = await CatalogReadSession.Open(db, CancellationToken.None);
            var changed = (
                await reader.ReadPages(next, [(collection, new(2))], CancellationToken.None)
            )[0];
            changed.Targets.Should().Equal(original.Targets.Skip(1).Take(2));
        });
    }

    private static async Task WithShowScanFixture(Func<NpgsqlConnection, Task> action)
    {
        await using var connection = await MigratedTestDatabase.OpenAsync();
        try
        {
            await connection.ExecuteAsync(
                """
                BEGIN;
                SET LOCAL norm.author_type='importer'; SET LOCAL norm.author_ref='v4-review';
                SET LOCAL norm.run_uuid='00000000-0000-0000-0000-000000000099';
                INSERT INTO shows(id,uuid,artist_id,venue_id,date,display_date,updated_at)
                SELECT 946000+i,('20000000-0000-0000-0000-'||lpad(i::text,12,'0'))::uuid,
                       925001,925001,DATE '2020-01-01'+i,(DATE '2020-01-01'+i)::text,'2026-01-01'
                FROM generate_series(1,150) i;
                INSERT INTO sources(id,uuid,show_id,artist_id,venue_id,is_soundboard,is_remaster,has_jamcharts,upstream_identifier,display_date,updated_at)
                SELECT 946000+i,('20000000-0000-0000-0001-'||lpad(i::text,12,'0'))::uuid,
                       946000+i,925001,925001,false,false,false,'show-scan-'||i,
                       (DATE '2020-01-01'+i)::text,'2026-01-01'
                FROM unnest(ARRAY[15,47,79,110,145]) i;
                INSERT INTO source_sets(id,uuid,source_id,index,name,updated_at)
                SELECT 946000+i,('20000000-0000-0000-0002-'||lpad(i::text,12,'0'))::uuid,
                       946000+i,1,'Set 1','2026-01-01' FROM unnest(ARRAY[15,47,79,110,145]) i;
                INSERT INTO source_tracks(id,uuid,artist_id,source_id,source_set_id,track_position,title,slug,mp3_url,updated_at,is_orphaned)
                SELECT 946000+i,('20000000-0000-0000-0003-'||lpad(i::text,12,'0'))::uuid,
                       925001,946000+i,946000+i,1,'Scarlet','scarlet','https://example.invalid/scan/'||i,'2026-01-01',false
                FROM unnest(ARRAY[15,47,79,110,145]) i;
                INSERT INTO norm.track_songs(track_uuid,part,song_id,text,resolved,basis,author_type,author_ref,run_uuid)
                SELECT ('20000000-0000-0000-0003-'||lpad(i::text,12,'0'))::uuid,1,
                       'song_00000101','Scarlet',true,'ruling','importer','v4-review','00000000-0000-0000-0000-000000000099'
                FROM unnest(ARRAY[15,47,79,110,145]) i;
                -- Deliberately stale overestimates force candidate discovery even for sparse songs.
                UPDATE norm.song_counts SET matched_track_count=100000,matched_show_count=100000
                WHERE song_id IN ('song_00000101','song_00000102','song_00000103');
                COMMIT;
                """
            );
            await action(connection);
        }
        finally
        {
            await connection.ExecuteAsync(
                """
                ROLLBACK;
                SET norm.author_type='importer'; SET norm.author_ref='v4-review';
                SET norm.run_uuid='00000000-0000-0000-0000-000000000099';
                DELETE FROM norm.track_songs WHERE track_uuid IN
                    (SELECT uuid FROM source_tracks WHERE id BETWEEN 946001 AND 946150);
                DELETE FROM source_tracks WHERE id BETWEEN 946001 AND 946150;
                DELETE FROM source_sets WHERE id BETWEEN 946001 AND 946150;
                DELETE FROM sources WHERE id BETWEEN 946001 AND 946150;
                DELETE FROM shows WHERE id BETWEEN 946001 AND 946150;
                SELECT norm.refresh_song_counts('00000000-0000-0000-0000-000000000301');
                """
            );
        }
    }
}
