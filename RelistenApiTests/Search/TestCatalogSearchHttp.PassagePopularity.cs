using Dapper;
using FluentAssertions;
using Relisten.Identifiers;
using Relisten.Search;

namespace RelistenApiTests.Search;

public sealed partial class TestCatalogSearchHttp
{
    [Test]
    public async Task PassageReadsPublishedShowSongStartsAndPrefersThePopularRecording()
    {
        await using var connection = await MigratedTestDatabase.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var source = Guid.NewGuid();
        var track = Guid.NewGuid();
        await connection.ExecuteAsync(
            """
            UPDATE artists SET slug='grateful-dead' WHERE id=910004;
            INSERT INTO sources(id,uuid,show_id,artist_id,venue_id,is_soundboard,is_remaster,
                has_jamcharts,upstream_identifier,display_date,updated_at)
            SELECT 919907,@source,show_id,artist_id,venue_id,is_soundboard,is_remaster,
                has_jamcharts,'popular-passage-fixture',display_date,updated_at
            FROM sources WHERE id=910004;
            INSERT INTO source_sets(id,uuid,source_id,index,name,updated_at)
            VALUES (919907,gen_random_uuid(),919907,1,'Set 1',now());
            INSERT INTO source_tracks(id,uuid,artist_id,source_id,source_set_id,track_position,
                duration,title,slug,mp3_url,updated_at)
            SELECT 919907,@track,artist_id,919907,919907,track_position,
                duration,title,slug,'https://example.invalid/popular-passage.mp3',updated_at
            FROM source_tracks WHERE id=910004;
            INSERT INTO norm.track_songs(track_uuid,part,song_id,text,segue_after,resolved,
                basis,author_type,author_ref,run_uuid)
            SELECT @track,part,song_id,text,segue_after,resolved,basis,author_type,author_ref,run_uuid
            FROM norm.track_songs WHERE track_uuid=(SELECT uuid FROM source_tracks WHERE id=910004);
            """,
            new { source, track },
            transaction
        );

        var rows = new SearchProjectionRows(connection, transaction);
        var venues = (await rows.Venues(CancellationToken.None)).ToDictionary(venue => venue.Id);
        var entries = (await rows.Songs(CancellationToken.None))
            .Where(song => song.Id is "song_00000003" or "song_00000004")
            .Select(song =>
                (
                    new SearchDocument
                    {
                        Id = song.Id,
                        Kind = "song",
                        ArtistSlug = "grateful-dead",
                        Name = SearchText.Fold(song.Name),
                        Label = song.Name,
                    },
                    Array.Empty<string>()
                )
            );
        var vocabulary = new SearchVocabulary(entries, []);
        var query = new SearchQuery("scarlet fire");

        var unpublished = await Load();
        unpublished.TrackPlays.Should().BeNull();
        unpublished.RecordingPlays.Should().BeNull();

        await connection.ExecuteAsync(
            """
            INSERT INTO song_performance_popularity(uuid,artist_uuid,song_id,plays_1y,generated_at,
                plays_30d,hours_30d,hot_score_30d,plays_7d,hours_7d,hot_score_7d,
                plays_48h,hours_48h,hot_score_48h,trend_ratio)
            SELECT h.uuid,a.uuid,plays.song,plays.total,now(),0,0,0,0,0,0,0,0,0,0
            FROM shows h JOIN artists a ON a.id=h.artist_id
            CROSS JOIN (VALUES ('song_00000003',11),('song_00000004',19)) plays(song,total)
            WHERE h.id=910004;
            INSERT INTO recording_popularity(uuid,artist_uuid,plays_1y,generated_at,
                plays_30d,hours_30d,hot_score_30d,plays_7d,hours_7d,hot_score_7d,
                plays_48h,hours_48h,hot_score_48h,trend_ratio)
            SELECT s.uuid,a.uuid,CASE WHEN s.id=919907 THEN 100 ELSE 2 END,now(),0,0,0,0,0,0,0,0,0,0
            FROM sources s JOIN artists a ON a.id=s.artist_id WHERE s.id IN (910004,919907);
            """,
            transaction: transaction
        );

        var published = await Load();
        published.TrackPlays.Should().Be(30);
        published.SourceId.Should().Be(UuidTypeId.Format("source", source));
        published.TrackIds.Should().Equal(UuidTypeId.Format("track", track));

        async Task<SearchPassage> Load()
        {
            var matches = await SearchPassages.Discover(
                connection,
                transaction,
                vocabulary,
                venues,
                CancellationToken.None
            );
            var ranked = await SearchPassages.RankByPublishedStarts(
                connection,
                transaction,
                matches,
                CancellationToken.None
            );
            return ranked.Find(query).Should().ContainSingle().Subject;
        }
    }
}
