using System.Net;
using System.Text.Json.Nodes;
using Dapper;
using FluentAssertions;
using Relisten.Identifiers;

namespace RelistenApiTests.V4;

public sealed partial class TestV4CatalogHttp
{
    [Test]
    public async Task Review_song_search_matches_a_name_that_ends_in_punctuation()
    {
        await using var connection = await MigratedTestDatabase.OpenAsync();
        var matchId = Guid.NewGuid();
        var decoyId = Guid.NewGuid();
        await ChangeNormalization(
            connection,
            """
            INSERT INTO norm.songs(id,artist_uuid,name,slug,kind,author_type,author_ref,run_uuid)
            VALUES ('song_0000trkn','00000000-0000-0000-0000-000000000014','Truckin''','truckin','song','importer','v4-review','00000000-0000-0000-0000-000000000099');
            """
        );
        await connection.ExecuteAsync(
            """
            INSERT INTO source_reviews (uuid, source_id, rating, review, updated_at)
            VALUES (@matchId, 910004, 9, 'Then Truckin'' opened the second set', now()),
                   (@decoyId, 910004, 9, 'Destruckin'' is not a song', now())
            """,
            new { matchId, decoyId }
        );
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                McpArtistPath("/reviews/search?song_id=song_0000trkn&show_id=" + ShowId(4))
            );
            // Review search limits each client; keep this request out of other tests' budget.
            request.Headers.Add("X-Forwarded-For", "192.0.2.30");
            using var response = await http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK, body);

            JsonNode
                .Parse(body)!
                .AsArray()
                .Select(review => review!["excerpt"]!.GetValue<string>())
                .Should()
                .Equal("Then Truckin' opened the second set");
        }
        finally
        {
            await connection.ExecuteAsync(
                "DELETE FROM source_reviews WHERE uuid IN (@matchId,@decoyId)",
                new { matchId, decoyId }
            );
            await ChangeNormalization(
                connection,
                "DELETE FROM norm.songs WHERE id='song_0000trkn'"
            );
        }
    }

    [Test]
    public async Task Performance_rating_cursor_pages_through_the_unpaged_order()
    {
        var path =
            "/api/v4/artists/"
            + UuidTypeId.Format("artist", Guid.Parse("00000000-0000-0000-0000-000000000301"))
            + "/performances?order=rating";
        static string Key(JsonNode item) =>
            item["show_id"]!.GetValue<string>() + "/" + item["song_id"]!.GetValue<string>();

        var unpaged = (await GetJson(path + "&limit=50"))["items"]!
            .AsArray()
            .Select(item => Key(item!))
            .ToArray();
        unpaged.Should().HaveCountGreaterThan(2);

        var paged = new List<string>();
        string? cursor = null;
        do
        {
            var page = await GetJson(
                path + "&limit=1" + (cursor is null ? "" : "&cursor=" + cursor)
            );
            paged.AddRange(page["items"]!.AsArray().Select(item => Key(item!)));
            cursor = page["next_cursor"]?.GetValue<string>();
        } while (cursor is not null && paged.Count <= unpaged.Length);

        paged.Should().Equal(unpaged);
    }

    [Test]
    public async Task Performance_date_pages_with_a_duration_filter_span_show_batches()
    {
        // Twenty long recordings exceed the first 16-show batch. The short recordings
        // between them must not count toward a batch or appear in the results.
        await using var connection = await MigratedTestDatabase.OpenAsync();
        await ChangeNormalization(
            connection,
            """
            INSERT INTO shows(uuid,artist_id,date,display_date,updated_at)
            SELECT ('00000000-0000-0000-0001-'||lpad(n::text,12,'0'))::uuid,910004,
                   '2001-01-01'::date+n,('2001-01-01'::date+n)::text,now()
            FROM generate_series(1,40) n;
            INSERT INTO sources(uuid,artist_id,show_id,upstream_identifier,display_date,is_soundboard,is_remaster,has_jamcharts,updated_at)
            SELECT sh.uuid,910004,sh.id,'multi-batch-'||sh.uuid,sh.display_date,false,false,false,now()
            FROM shows sh WHERE sh.uuid::text LIKE '00000000-0000-0000-0001-%';
            INSERT INTO source_sets(uuid,source_id,index,name,updated_at)
            SELECT s.uuid,s.id,1,'Set 1',now() FROM sources s WHERE s.upstream_identifier LIKE 'multi-batch-%';
            INSERT INTO source_tracks(uuid,artist_id,source_id,source_set_id,track_position,duration,title,slug,mp3_url,updated_at)
            SELECT s.uuid,910004,s.id,sets.id,1,
                   CASE WHEN right(s.uuid::text,2)::int % 2 = 0 THEN 1200 ELSE 300 END,
                   'Fire on the Mountain','multi-batch','https://example.invalid/multi-batch/'||s.uuid||'.mp3',now()
            FROM sources s JOIN source_sets sets ON sets.source_id=s.id
            WHERE s.upstream_identifier LIKE 'multi-batch-%';
            INSERT INTO norm.track_songs(track_uuid,part,song_id,text,resolved,basis,author_type,author_ref,run_uuid)
            SELECT t.uuid,1,'song_00000004','Fire on the Mountain',true,'song_name','importer','v4-review','00000000-0000-0000-0000-000000000099'
            FROM source_tracks t WHERE t.slug='multi-batch';
            """
        );
        try
        {
            var path = McpArtistPath("/performances?min_minutes=15");
            static string Key(JsonNode item) =>
                item["show_id"]!.GetValue<string>() + "/" + item["song_id"]!.GetValue<string>();

            var unpaged = (await GetJson(path + "&limit=50"))["items"]!.AsArray();
            unpaged
                .Select(item => item!["date"]!.GetValue<string>())
                .Should()
                .HaveCount(20)
                .And.OnlyHaveUniqueItems()
                .And.BeInDescendingOrder();
            unpaged
                .Select(item => item!["representative"]!["file_duration_seconds"]!.GetValue<int>())
                .Should()
                .AllBeEquivalentTo(1200);

            var paged = new List<string>();
            string? cursor = null;
            do
            {
                var page = await GetJson(
                    path + "&limit=3" + (cursor is null ? "" : "&cursor=" + cursor)
                );
                paged.AddRange(page["items"]!.AsArray().Select(item => Key(item!)));
                cursor = page["next_cursor"]?.GetValue<string>();
            } while (cursor is not null && paged.Count <= unpaged.Count);

            paged.Should().Equal(unpaged.Select(item => Key(item!)));
        }
        finally
        {
            await ChangeNormalization(
                connection,
                """
                DELETE FROM norm.track_songs WHERE track_uuid IN (SELECT uuid FROM source_tracks WHERE slug='multi-batch');
                DELETE FROM source_tracks WHERE slug='multi-batch';
                DELETE FROM source_sets WHERE source_id IN (SELECT id FROM sources WHERE upstream_identifier LIKE 'multi-batch-%');
                DELETE FROM sources WHERE upstream_identifier LIKE 'multi-batch-%';
                DELETE FROM shows WHERE uuid::text LIKE '00000000-0000-0000-0001-%';
                """
            );
        }
    }
}
