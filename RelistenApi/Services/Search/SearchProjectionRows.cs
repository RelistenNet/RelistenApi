using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Catalog;

namespace Relisten.Search;

// The larger inputs are read in show order so the projector can discard each show's files.
internal sealed class SearchProjectionRows(
    NpgsqlConnection connection,
    NpgsqlTransaction transaction,
    int? artistId = null
)
{
    public int? ArtistId => artistId;
    internal CatalogReadSession DisplaySession =>
        CatalogReadSession.Borrow(connection, transaction);
    internal NpgsqlConnection Connection => connection;
    internal NpgsqlTransaction Transaction => transaction;

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

    public async Task<IEnumerable<Venue>> Venues(CancellationToken cancellationToken) =>
        SearchReviewedPlaceNames.WithVenueSpellings(
            await Read<Venue>(
                """
                WITH reviewed_keys AS MATERIALIZED (
                    SELECT * FROM jsonb_to_recordset(@VenueKeysJson::jsonb) AS k(venue_id text, normalized text)
                )
                SELECT v.id, v.name, v.city, v.region, v.country, v.merged_into AS MergedInto,
                       ARRAY(
                           SELECT DISTINCT n.name FROM norm.venue_names n
                           WHERE n.venue_id = v.id AND (
                               n.kind = 'official' OR NOT EXISTS (
                                   SELECT 1 FROM reviewed_keys k WHERE k.venue_id = n.venue_id AND k.normalized = n.normalized
                               )
                           )
                           ORDER BY 1
                       ) AS AliasValues
                FROM norm.venues v ORDER BY v.id
                """,
                cancellationToken,
                new { SearchReviewedPlaceNames.VenueKeysJson }
            )
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

    public async Task<IEnumerable<ShortPlaceName>> ShortVenueNames(
        CancellationToken cancellationToken
    ) =>
        SearchReviewedPlaceNames.WithShortNames(
            await Read<ShortPlaceName>(
                """
                SELECT venue_id AS VenueId, name, normalized, false AS ExplicitlyApproved
                FROM norm.venue_names
                WHERE kind = 'short'
                """,
                cancellationToken
            )
        );

    public Task<IEnumerable<ImportedPlace>> ImportedPlacesForAliases(
        CancellationToken cancellationToken
    ) =>
        Read<ImportedPlace>(
            """
            SELECT v.uuid, v.location, e.venue_id AS MappedVenueId
            FROM venues v LEFT JOIN norm.venue_evidence e ON e.legacy_venue_uuid = v.uuid
            WHERE v.location IS NOT NULL OR e.venue_id IS NOT NULL
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
                   CASE WHEN f.norm_catalogue THEN v.venue_id END AS CanonicalVenueId,
                   coalesce(s.avg_rating, 0) AS AvgRating
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
                   coalesce(s.is_soundboard, false) AS Soundboard, coalesce(s.avg_rating_weighted, 0) AS Rating,
                   coalesce(s.avg_rating, 0) AS AvgRating, coalesce(s.num_reviews, 0) AS NumReviews
            FROM sources s JOIN shows h ON h.id = s.show_id AND h.artist_id = s.artist_id
            WHERE @ArtistId IS NULL OR s.artist_id = @ArtistId
            ORDER BY s.id
            """,
            cancellationToken
        );

    public Task<IEnumerable<CatalogSource>> RecordingTexts(
        long[] sourceIds,
        CancellationToken cancellationToken
    ) =>
        Read<CatalogSource>(
            CatalogRecordingReader.SourceSelect + " WHERE src.id=ANY(@SourceIds) ORDER BY src.id",
            cancellationToken,
            new { SourceIds = sourceIds }
        );

    public Task<IEnumerable<long>> SourceVenueIds(CancellationToken cancellationToken) =>
        Read<long>(
            """
            SELECT DISTINCT venue_id FROM sources src
            WHERE venue_id IS NOT NULL AND (@ArtistId IS NULL OR src.artist_id=@ArtistId)
            """,
            cancellationToken
        );

    public IAsyncEnumerable<Review> Reviews() =>
        Stream<Review>(
            """
            SELECT r.uuid, s.show_id AS ShowId, r.source_id AS SourceId, r.title, r.review AS Body,
                   r.author, r.rating::int AS Rating, r.created_at AS CreatedAt, r.updated_at AS UpdatedAt
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

    private const string PlayableTracksAtShows = """
        FROM sources s JOIN shows h ON h.id = s.show_id AND h.artist_id = s.artist_id
        JOIN source_tracks t ON t.source_id = s.id AND t.artist_id = s.artist_id
        JOIN source_sets ss ON ss.id = t.source_set_id AND ss.source_id = s.id
        WHERE s.show_id=ANY(@ShowIds) AND NOT t.is_orphaned
          AND (nullif(t.mp3_url, '') IS NOT NULL OR nullif(t.flac_url, '') IS NOT NULL)
        """;

    public Task<IEnumerable<Track>> Tracks(long[] showIds, CancellationToken cancellationToken) =>
        Read<Track>(
            $"""
            SELECT s.show_id AS ShowId, s.id AS SourceId, t.uuid, t.slug, coalesce(t.title, '') AS title,
                   coalesce(t.duration, 0) AS duration, coalesce(t.track_position, 0) AS position,
                   coalesce(ss.index, 0) AS SetIndex, coalesce(ss.name, '') AS SetName
            {PlayableTracksAtShows}
            ORDER BY s.show_id, s.id, ss.index, t.track_position, t.uuid
            """,
            cancellationToken,
            new { ShowIds = showIds }
        );

    public Task<IEnumerable<TrackRankingInput>> RankingTracks(
        long[] showIds,
        CancellationToken cancellationToken
    ) =>
        Read<TrackRankingInput>(
            $"""
            SELECT s.show_id AS ShowId, t.uuid, coalesce(t.title, '') AS Title
            {PlayableTracksAtShows}
            """,
            cancellationToken,
            new { ShowIds = showIds }
        );

    public Task<IEnumerable<ImportedSongMembership>> ImportedSongsAtShows(
        long[] showIds,
        CancellationToken cancellationToken
    ) =>
        Read<ImportedSongMembership>(
            """
            SELECT DISTINCT sh.id AS ShowId, song.uuid, song.name
            FROM shows sh
            JOIN setlist_shows ss ON ss.artist_id=sh.artist_id AND ss.date=sh.date
            JOIN setlist_songs_plays p ON p.played_setlist_show_id=ss.id
            JOIN setlist_songs song ON song.id=p.played_setlist_song_id AND song.artist_id=sh.artist_id
            WHERE sh.id=ANY(@ShowIds)
            """,
            cancellationToken,
            new { ShowIds = showIds }
        );

    public Task<IEnumerable<ImportedSong>> ImportedSongs(CancellationToken cancellationToken) =>
        Read<ImportedSong>(
            """
            SELECT s.id, s.uuid, s.artist_id AS ArtistId, a.uuid AS ArtistUuid, s.name,
                   coalesce(c.setlist_show_count, 0) AS shows, s.slug,
                   s.upstream_identifier AS UpstreamIdentifier, s.created_at AS CreatedAt,
                   s.updated_at AS UpdatedAt
            FROM setlist_songs s JOIN artists a ON a.id=s.artist_id
            LEFT JOIN setlist_song_counts c ON c.setlist_song_id = s.id
            WHERE @ArtistId IS NULL OR s.artist_id = @ArtistId
            ORDER BY s.id
            """,
            cancellationToken
        );

    public Task<IEnumerable<ReviewedSetlistSong>> ReviewedSetlistSongs(
        CancellationToken cancellationToken
    ) =>
        Read<ReviewedSetlistSong>(
            """
            SELECT s.id, s.uuid, a.slug AS ArtistSlug
            FROM setlist_songs s JOIN artists a ON a.id = s.artist_id
            WHERE s.id = ANY(@Ids)
            """,
            cancellationToken,
            new { Ids = SearchReviewedNicknames.SetlistSongIds }
        );

    internal sealed record ReviewedSetlistSong(long Id, Guid Uuid, string ArtistSlug);

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

    private Task<IEnumerable<T>> Read<T>(
        string sql,
        CancellationToken cancellationToken,
        object? parameters = null
    ) =>
        connection.QueryAsync<T>(
            new CommandDefinition(
                sql,
                parameters ?? new { ArtistId },
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

    internal static Venue? CurrentVenue(IReadOnlyDictionary<string, Venue> venues, string? id)
    {
        // Bound traversal if malformed venue merges form a cycle.
        for (var depth = 0; depth < 32 && !string.IsNullOrEmpty(id); depth++)
        {
            var venue = venues.GetValueOrDefault(id);
            if (string.IsNullOrEmpty(venue?.MergedInto))
            {
                return venue;
            }

            id = venue.MergedInto;
        }

        return null;
    }

    internal sealed record ShortPlaceName(
        string VenueId,
        string Name,
        string Normalized,
        bool ExplicitlyApproved
    );

    internal sealed record CityPlaceName(
        string City,
        string Region,
        string Country,
        string Name,
        string Normalized
    );

    internal sealed record ImportedPlace(Guid Uuid, string? Location, string? MappedVenueId);

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

        public string LocationLabel =>
            string.Join(
                ", ",
                new[] { City, Region, Country }.Where(part => !string.IsNullOrEmpty(part))
            );
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
        string? CanonicalVenueId,
        float AvgRating
    );

    internal sealed record Recording(
        long Id,
        Guid Uuid,
        long ShowId,
        string? Upstream,
        bool Soundboard,
        float Rating,
        float AvgRating,
        int NumReviews
    );

    internal sealed record Review(
        Guid Uuid,
        long ShowId,
        long SourceId,
        string? Title,
        string? Body,
        string? Author,
        int? Rating,
        DateTime CreatedAt,
        DateTime UpdatedAt
    );

    internal sealed record SongMapping(Guid TrackUuid, string SongId);

    internal sealed record TrackRankingInput(long ShowId, Guid Uuid, string Title);

    internal sealed record Track(
        long ShowId,
        long SourceId,
        Guid Uuid,
        string Slug,
        string Title,
        int Duration,
        int Position,
        int SetIndex,
        string SetName
    );

    internal sealed record ImportedSongMembership(long ShowId, Guid Uuid, string Name);

    internal sealed record ImportedSong(
        long Id,
        Guid Uuid,
        int ArtistId,
        Guid ArtistUuid,
        string Name,
        int Shows,
        string Slug,
        string UpstreamIdentifier,
        DateTime CreatedAt,
        DateTime UpdatedAt
    );
}
