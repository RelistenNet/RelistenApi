using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;

namespace Relisten.Search;

// The larger inputs are read in show order so the projector can discard each show's files.
internal sealed class SearchProjectionRows(
    NpgsqlConnection connection,
    NpgsqlTransaction transaction,
    int? artistId = null
)
{
    public int? ArtistId => artistId;

    public static async Task<NpgsqlTransaction> BeginSnapshot(
        NpgsqlConnection connection,
        CancellationToken cancellationToken
    )
    {
        var snapshot = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken
        );
        try
        {
            await connection.ExecuteAsync(
                new CommandDefinition(
                    "SET TRANSACTION READ ONLY",
                    transaction: snapshot,
                    cancellationToken: cancellationToken
                )
            );
            return snapshot;
        }
        catch
        {
            await snapshot.DisposeAsync();
            throw;
        }
    }

    public Task<IEnumerable<Artist>> Artists(CancellationToken cancellationToken) =>
        Read<Artist>(
            """
            SELECT a.id, a.uuid, a.slug, a.name, coalesce(f.norm_catalogue, false) AS approved
            FROM artists a LEFT JOIN features f ON f.artist_id = a.id
            WHERE @ArtistId IS NULL OR a.id = @ArtistId
            ORDER BY a.id
            """,
            cancellationToken
        );

    public Task<IEnumerable<Venue>> Venues(CancellationToken cancellationToken) =>
        Read<Venue>(
            """
            SELECT v.id, v.name, v.city, v.region, v.country, v.merged_into AS MergedInto,
                   ARRAY(SELECT DISTINCT n.name FROM norm.venue_names n WHERE n.venue_id = v.id ORDER BY 1) AS AliasValues
            FROM norm.venues v ORDER BY v.id
            """,
            cancellationToken
        );

    public Task<IEnumerable<ImportedVenue>> ImportedVenues(CancellationToken cancellationToken) =>
        Read<ImportedVenue>(
            """
            SELECT id, uuid, artist_id AS ArtistId, name, location, past_names AS PastNames
            FROM venues
            WHERE @ArtistId IS NULL OR artist_id = @ArtistId
               OR id IN (SELECT venue_id FROM shows WHERE artist_id = @ArtistId)
            ORDER BY id
            """,
            cancellationToken
        );

    public Task<IEnumerable<Tour>> Tours(CancellationToken cancellationToken) =>
        Read<Tour>(
            """
            SELECT id, uuid, artist_id AS ArtistId, name FROM tours
            WHERE @ArtistId IS NULL OR artist_id = @ArtistId
            ORDER BY id
            """,
            cancellationToken
        );

    public Task<IEnumerable<Song>> Songs(CancellationToken cancellationToken) =>
        Read<Song>(
            """
            SELECT s.id, s.artist_uuid AS ArtistUuid, s.name, s.merged_into AS MergedInto,
                   coalesce(c.matched_show_count, 0) AS shows,
                   ARRAY(SELECT DISTINCT a.alias FROM norm.song_aliases a WHERE a.song_id = s.id ORDER BY 1) AS AliasValues
            FROM norm.songs s LEFT JOIN norm.song_counts c ON c.song_id = s.id
            WHERE @ArtistId IS NULL OR s.artist_uuid = (SELECT uuid FROM artists WHERE id = @ArtistId)
            ORDER BY s.id
            """,
            cancellationToken
        );

    public Task<IEnumerable<Show>> Shows(CancellationToken cancellationToken) =>
        Read<Show>(
            """
            SELECT s.id, s.uuid, s.artist_id AS ArtistId, s.venue_id::bigint AS VenueId,
                   s.date AS date, s.tour_id AS TourId,
                   CASE WHEN f.norm_catalogue THEN v.venue_id END AS CanonicalVenueId
            FROM shows s LEFT JOIN norm.show_venues v ON v.show_uuid = s.uuid
            LEFT JOIN features f ON f.artist_id = s.artist_id
            WHERE @ArtistId IS NULL OR s.artist_id = @ArtistId
            ORDER BY s.id
            """,
            cancellationToken
        );

    public Task<IEnumerable<Recording>> Recordings(CancellationToken cancellationToken) =>
        Read<Recording>(
            """
            SELECT s.id, s.uuid, s.show_id AS ShowId, s.upstream_identifier AS Upstream,
                   coalesce(s.is_soundboard, false) AS Soundboard, coalesce(s.avg_rating_weighted, 0) AS Rating
            FROM sources s JOIN shows h ON h.id = s.show_id AND h.artist_id = s.artist_id
            WHERE @ArtistId IS NULL OR s.artist_id = @ArtistId
            ORDER BY s.id
            """,
            cancellationToken
        );

    public IAsyncEnumerable<RecordingText> RecordingTexts() =>
        Stream<RecordingText>(
            """
            SELECT s.id, s.taper, s.transferrer, s.description, s.taper_notes AS TaperNotes, s.lineage, s.source
            FROM sources s JOIN shows h ON h.id = s.show_id AND h.artist_id = s.artist_id
            WHERE @ArtistId IS NULL OR s.artist_id = @ArtistId
            ORDER BY s.id
            """
        );

    public IAsyncEnumerable<Review> Reviews() =>
        Stream<Review>(
            """
            SELECT r.uuid, s.show_id AS ShowId, r.source_id AS SourceId, r.title, r.review AS Body
            FROM source_reviews r JOIN sources s ON s.id = r.source_id
            JOIN shows h ON h.id = s.show_id AND h.artist_id = s.artist_id
            WHERE @ArtistId IS NULL OR s.artist_id = @ArtistId
            ORDER BY s.show_id, r.id
            """
        );

    public Task<IEnumerable<SongMapping>> Mappings(CancellationToken cancellationToken) =>
        Read<SongMapping>(
            """
            SELECT m.track_uuid AS TrackUuid, m.song_id AS SongId
            FROM artists a JOIN features f ON f.artist_id = a.id AND f.norm_catalogue
            CROSS JOIN LATERAL norm.song_recording_matches(a.uuid, NULL::text[]) m
            WHERE @ArtistId IS NULL OR a.id = @ArtistId
            """,
            cancellationToken
        );

    public IAsyncEnumerable<Track> Tracks() =>
        Stream<Track>(
            """
            SELECT s.show_id AS ShowId, s.id AS SourceId, t.uuid, coalesce(t.title, '') AS title,
                   coalesce(t.duration, 0) AS duration, coalesce(t.track_position, 0) AS position,
                   coalesce(ss.index, 0) AS SetIndex, coalesce(ss.name, '') AS SetName
            FROM sources s JOIN shows h ON h.id = s.show_id AND h.artist_id = s.artist_id
            JOIN source_tracks t ON t.source_id = s.id AND t.artist_id = s.artist_id
            JOIN source_sets ss ON ss.id = t.source_set_id AND ss.source_id = s.id
            WHERE NOT t.is_orphaned
              AND (@ArtistId IS NULL OR s.artist_id = @ArtistId)
              AND (nullif(t.mp3_url, '') IS NOT NULL OR nullif(t.flac_url, '') IS NOT NULL)
            ORDER BY s.show_id, s.id, ss.index, t.track_position, t.uuid
            """
        );

    public IAsyncEnumerable<ImportedSong> ImportedSongs() =>
        Stream<ImportedSong>(
            """
            SELECT s.uuid, s.artist_id AS ArtistId, s.name, coalesce(c.setlist_show_count, 0) AS shows
            FROM setlist_songs s LEFT JOIN setlist_song_counts c ON c.setlist_song_id = s.id
            WHERE @ArtistId IS NULL OR s.artist_id = @ArtistId
            ORDER BY s.id
            """
        );

    public Task<IEnumerable<VenueMembership>> VenueMemberships(
        string[] venueIds,
        CancellationToken cancellationToken
    ) =>
        connection.QueryAsync<VenueMembership>(
            new CommandDefinition(
                """
                SELECT v.venue_id AS VenueId, a.slug AS ArtistSlug, count(*)::int AS Shows
                FROM norm.show_venues v JOIN shows s ON s.uuid = v.show_uuid
                JOIN artists a ON a.id = s.artist_id
                JOIN features f ON f.artist_id = a.id AND f.norm_catalogue
                WHERE v.venue_id = ANY(@VenueIds)
                GROUP BY v.venue_id, a.slug
                """,
                new { VenueIds = venueIds },
                transaction,
                cancellationToken: cancellationToken
            )
        );

    private Task<IEnumerable<T>> Read<T>(string sql, CancellationToken cancellationToken) =>
        connection.QueryAsync<T>(
            new CommandDefinition(
                sql,
                new { ArtistId },
                transaction: transaction,
                commandTimeout: 600,
                cancellationToken: cancellationToken
            )
        );

    private IAsyncEnumerable<T> Stream<T>(string sql) =>
        connection.QueryUnbufferedAsync<T>(
            sql,
            new { ArtistId },
            transaction: transaction,
            commandTimeout: 600
        );

    internal sealed record Artist(int Id, Guid Uuid, string Slug, string Name, bool Approved);

    internal sealed record VenueMembership(string VenueId, string ArtistSlug, int Shows);

    internal sealed record Venue(
        string Id,
        string Name,
        string? City,
        string? Region,
        string? Country,
        string? MergedInto,
        Array AliasValues
    )
    {
        public string[] Aliases => (string[])AliasValues;
    }

    internal sealed record ImportedVenue(
        long Id,
        Guid Uuid,
        int ArtistId,
        string? Name,
        string? Location,
        string? PastNames
    );

    internal sealed record Tour(long Id, Guid Uuid, int ArtistId, string Name);

    internal sealed record Song(
        string Id,
        Guid ArtistUuid,
        string Name,
        string? MergedInto,
        int Shows,
        Array AliasValues
    )
    {
        public string[] Aliases => (string[])AliasValues;
    }

    internal sealed record Show(
        long Id,
        Guid Uuid,
        int ArtistId,
        long? VenueId,
        DateOnly Date,
        long? TourId,
        string? CanonicalVenueId
    );

    internal sealed record Recording(
        long Id,
        Guid Uuid,
        long ShowId,
        string? Upstream,
        bool Soundboard,
        float Rating
    );

    internal sealed record RecordingText(
        long Id,
        string? Taper,
        string? Transferrer,
        string? Description,
        string? TaperNotes,
        string? Lineage,
        string? Source
    );

    internal sealed record Review(
        Guid Uuid,
        long ShowId,
        long SourceId,
        string? Title,
        string? Body
    );

    internal sealed record SongMapping(Guid TrackUuid, string SongId);

    internal sealed record Track(
        long ShowId,
        long SourceId,
        Guid Uuid,
        string Title,
        int Duration,
        int Position,
        int SetIndex,
        string SetName
    );

    internal sealed record ImportedSong(Guid Uuid, int ArtistId, string Name, int Shows);
}
