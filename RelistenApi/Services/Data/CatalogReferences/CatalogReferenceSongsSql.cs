namespace Relisten.Data
{
    internal static class CatalogReferenceSongsSql
    {
        public const string Query = CatalogReferenceSql.Requested + @"
SELECT
    song.*,
    artist.uuid AS artist_uuid,
    (
        SELECT COUNT(show_entity.id)::integer
        FROM setlist_songs_plays play
        JOIN setlist_shows setlist_show ON setlist_show.id = play.played_setlist_show_id
        LEFT JOIN shows show_entity
            ON show_entity.date = setlist_show.date
            AND show_entity.artist_id = song.artist_id
        WHERE play.played_setlist_song_id = song.id
    ) AS shows_played_at
FROM requested r
JOIN setlist_songs song ON r.catalog_type = 'song' AND song.uuid = r.catalog_uuid
JOIN artists artist ON artist.id = song.artist_id
JOIN features feature ON feature.artist_id = artist.id
ORDER BY song.uuid;";
    }
}
