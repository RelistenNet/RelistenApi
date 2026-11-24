using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.AspNetCore.WebUtilities;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Npgsql;
using Relisten.Api.Models.Api.V4;
using Relisten.Identifiers;
using Relisten.Services.Popularity;

namespace Relisten.Catalog;

public sealed record CatalogPerformanceQuery(
    string? SongId,
    int[]? Years,
    Guid? TourUuid,
    string? VenueId,
    double? MinSeconds,
    double? MaxSeconds,
    bool SoundboardOnly,
    string? SeguedFrom,
    string? SeguedInto,
    string Order,
    string? Seed,
    Guid[] ExcludedShows,
    Guid[] ExcludedTracks,
    int Limit,
    CatalogPerformanceCursor? After
);

/// <summary>The sort value and immutable tie-breakers of the last show/song returned.</summary>
public sealed record CatalogPerformanceCursor(decimal Score, DateOnly Date, Guid Show, string Song)
{
    public string Encode() =>
        WebEncoders.Base64UrlEncode(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(this));

    public static bool TryRead(string? value, out CatalogPerformanceCursor? cursor)
    {
        cursor = null;
        if (value is null)
        {
            return true;
        }
        if (value.Length > 512)
        {
            return false;
        }
        try
        {
            cursor = System.Text.Json.JsonSerializer.Deserialize<CatalogPerformanceCursor>(
                WebEncoders.Base64UrlDecode(value)
            );
            return cursor?.Song is { Length: > 0 and <= 50 }
                && cursor.Show != Guid.Empty
                && cursor.Date != default;
        }
        catch (Exception exception)
            when (exception is FormatException or System.Text.Json.JsonException)
        {
            return false;
        }
    }
}

/// <summary>
/// Reads one page of performances: an artist's playable recordings of one song at
/// one show, ranked by the requested order.
/// </summary>
/// <remarks>
/// Cursor protocol: every order ranks by (score, date, show, song ID) descending,
/// and the cursor is that tuple for the last returned row. The ranked-row query
/// returns only rows strictly after the cursor tuple. Each read asks for one row
/// more than the limit; that extra row decides whether a next cursor exists and
/// is not returned. A date-ordered page reads shows in batches; the first batch
/// includes the cursor's show, because that show's remaining songs may follow the
/// cursor, and the tuple filter drops the songs already returned.
/// </remarks>
public sealed class CatalogPerformances(DbService db, CatalogSongMetadataReader songs)
{
    private const int FirstShowBatchSize = 16;
    private const int MaxShowBatchSize = 256;

    // A page may take several statements: show batches, ranked rows, and plays.
    // The overall budget bounds their sum; the per-statement timeout stops one slow
    // statement before it uses the whole budget. Both report the same timeout.
    private static readonly TimeSpan ReadBudget = TimeSpan.FromSeconds(3);
    private const string StatementTimeout = "2s";

    public async Task<CatalogPerformancePage?> Read(
        Guid artistUuid,
        CatalogPerformanceQuery query,
        CancellationToken cancellationToken
    )
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(ReadBudget);
        try
        {
            return await ReadWithinBudget(artistUuid, query, budget.Token);
        }
        catch (Exception exception)
            when (!cancellationToken.IsCancellationRequested
                && (
                    exception is PostgresException { SqlState: PostgresErrorCodes.QueryCanceled }
                    || exception is OperationCanceledException && budget.IsCancellationRequested
                )
            )
        {
            throw new CatalogPerformanceTimeoutException();
        }
    }

    private async Task<CatalogPerformancePage?> ReadWithinBudget(
        Guid artistUuid,
        CatalogPerformanceQuery query,
        CancellationToken cancellationToken
    )
    {
        await using var session = await CatalogReadSession.Open(db, cancellationToken);
        await session.Connection.ExecuteAsync(
            session.Command(
                $"SET LOCAL statement_timeout = '{StatementTimeout}'",
                null,
                cancellationToken
            )
        );

        var artist = await ReadArtist(session, artistUuid, cancellationToken);
        if (artist is null)
        {
            return null;
        }
        if (!artist.Approved)
        {
            return new([], null);
        }

        var scope = await ReadSongScope(session, artistUuid, query, cancellationToken);
        if (scope is null)
        {
            return new([], null);
        }

        var rows = await ReadRows(session, artistUuid, artist.Id, query, scope, cancellationToken);
        var page = rows.Take(query.Limit).ToArray();
        var popularity = await ReadPopularity(session, page, cancellationToken);

        return new(
            page.Select((row, index) => Project(row, artist.Slug, popularity[index])).ToArray(),
            rows.Length > page.Length ? NextCursor(page[^1]) : null
        );
    }

    private static Task<Artist?> ReadArtist(
        CatalogReadSession session,
        Guid artistUuid,
        CancellationToken cancellationToken
    ) =>
        session.Connection.QuerySingleOrDefaultAsync<Artist?>(
            session.Command(
                """
                SELECT a.id, a.slug, coalesce(f.norm_catalogue,false) AS approved
                FROM artists a LEFT JOIN features f ON f.artist_id=a.id WHERE a.uuid=@artistUuid
                """,
                new { artistUuid },
                cancellationToken
            )
        );

    // Resolves the song, segued-from and segued-into IDs. Null when any of them is
    // unknown or belongs to another artist: such a filter matches nothing.
    private async Task<IReadOnlyDictionary<string, CatalogSong>?> ReadSongScope(
        CatalogReadSession session,
        Guid artistUuid,
        CatalogPerformanceQuery query,
        CancellationToken cancellationToken
    )
    {
        var songIds = new[] { query.SongId, query.SeguedFrom, query.SeguedInto }
            .OfType<string>()
            .ToArray();
        var scope = await songs.Read(session, songIds, cancellationToken);

        var allOwned = songIds.All(id =>
            scope.TryGetValue(id, out var song) && song.artist_uuid == artistUuid
        );
        return allOwned ? scope : null;
    }

    // Date order without a song filter reads the newest shows in batches and stops
    // once the page fills, instead of matching every song in the catalog. A song
    // filter starts matching from that song's own track rows, a bounded set, so one
    // ranked query reads it directly. Score orders must rank every candidate before
    // any page, so they use one query too.
    private static Task<PerformanceRow[]> ReadRows(
        CatalogReadSession session,
        Guid artistUuid,
        int artistId,
        CatalogPerformanceQuery query,
        IReadOnlyDictionary<string, CatalogSong> scope,
        CancellationToken cancellationToken
    ) =>
        query.Order == "date" && query.SongId is null
            ? ReadDatePage(session, artistUuid, artistId, query, scope, cancellationToken)
            : ReadRankedRows(
                session,
                artistUuid,
                artistId,
                query,
                scope,
                showBatch: null,
                query.Limit + 1,
                cancellationToken
            );

    private static string NextCursor(PerformanceRow last) =>
        new CatalogPerformanceCursor(
            last.Score,
            DateOnly.FromDateTime(last.Date),
            last.ShowUuid,
            last.SongId
        ).Encode();

    private static async Task<PerformanceRow[]> ReadDatePage(
        CatalogReadSession session,
        Guid artistUuid,
        int artistId,
        CatalogPerformanceQuery query,
        IReadOnlyDictionary<string, CatalogSong> scope,
        CancellationToken cancellationToken
    )
    {
        var target = query.Limit + 1;
        var matches = new List<PerformanceRow>(target);
        var position = new ShowPosition(
            query.After?.Date.ToDateTime(TimeOnly.MinValue),
            query.After?.Show,
            IncludeBoundary: true
        );
        var batchSize = FirstShowBatchSize;

        // Date and show UUID sort before song ID, so contiguous show batches
        // concatenate in final order (see the cursor protocol on the class).
        while (matches.Count < target)
        {
            var batch = await ReadShowBatch(
                session,
                artistUuid,
                artistId,
                query,
                position,
                batchSize,
                cancellationToken
            );
            if (batch.Length == 0)
            {
                break;
            }

            var rows = await ReadRankedRows(
                session,
                artistUuid,
                artistId,
                query,
                scope,
                batch.Select(show => show.Show).ToArray(),
                target - matches.Count,
                cancellationToken
            );
            matches.AddRange(rows);
            if (batch.Length < batchSize)
            {
                break;
            }

            // Filters the show query cannot see, such as segues, can leave most
            // batches empty. Doubling bounds the round trips across a large
            // catalog; the cap keeps each ranked-row statement well inside the
            // statement timeout.
            position = new ShowPosition(batch[^1].Date, batch[^1].Show, IncludeBoundary: false);
            batchSize = Math.Min(batchSize * 2, MaxShowBatchSize);
        }

        return matches.ToArray();
    }

    // The EXISTS clause applies the per-file filters before a show enters a batch.
    // It is a necessary condition for an eligible match, so it never drops a show
    // that ReadRankedRows would return. Without it, a selective duration or soundboard
    // filter reads most of a large catalog 16 shows at a time.
    private static async Task<ShowBoundary[]> ReadShowBatch(
        CatalogReadSession session,
        Guid artistUuid,
        int artistId,
        CatalogPerformanceQuery query,
        ShowPosition position,
        int batchSize,
        CancellationToken cancellationToken
    )
    {
        var shows = await session.Connection.QueryAsync<ShowBoundary>(
            session.Command(
                $"""
                WITH RECURSIVE {VenueMap}, show_scope AS ({ShowScopeSql})
                SELECT sc.uuid AS Show, sc.date::timestamp AS Date
                FROM show_scope sc
                WHERE (@afterDate::date IS NULL OR (sc.date,sc.uuid)<(@afterDate::date,@afterShow)
                    OR (@includeBoundary AND (sc.date,sc.uuid)=(@afterDate::date,@afterShow)))
                  AND EXISTS (
                    SELECT 1 FROM sources s JOIN source_tracks t ON t.source_id=s.id
                    WHERE s.show_id=sc.id AND NOT t.is_orphaned
                      AND (t.mp3_url IS NOT NULL OR t.flac_url IS NOT NULL)
                      AND NOT t.uuid=ANY(@ExcludedTracks)
                      AND (NOT @SoundboardOnly OR s.is_soundboard)
                      AND (@MinSeconds::double precision IS NULL OR t.duration>=@MinSeconds)
                      AND (@MaxSeconds::double precision IS NULL OR t.duration<=@MaxSeconds)
                  )
                ORDER BY sc.date DESC, sc.uuid DESC
                LIMIT @batchSize
                """,
                new
                {
                    artistUuid,
                    artistId,
                    query.ExcludedShows,
                    query.ExcludedTracks,
                    query.SoundboardOnly,
                    query.MinSeconds,
                    query.MaxSeconds,
                    query.Years,
                    query.TourUuid,
                    query.VenueId,
                    venueUuid = ImportedVenueUuid(query.VenueId),
                    afterDate = position.Date,
                    afterShow = position.Show,
                    includeBoundary = position.IncludeBoundary,
                    batchSize,
                },
                cancellationToken
            )
        );
        return shows.ToArray();
    }

    private static Guid? ImportedVenueUuid(string? venueId) =>
        venueId is { } venue && UuidTypeId.TryParse(venue, ImportedVenueId.Prefix, out var uuid)
            ? uuid
            : null;

    private sealed record ShowBoundary(Guid Show, DateTime Date);

    private sealed record ShowPosition(DateTime? Date, Guid? Show, bool IncludeBoundary);

    // Reads ranked performance rows after the cursor, for the given shows or, when
    // showBatch is null, for every show the query's filters allow.
    private static async Task<PerformanceRow[]> ReadRankedRows(
        CatalogReadSession session,
        Guid artistUuid,
        int artistId,
        CatalogPerformanceQuery query,
        IReadOnlyDictionary<string, CatalogSong> scope,
        Guid[]? showBatch,
        int size,
        CancellationToken cancellationToken
    )
    {
        string? CanonicalId(string? id) => id is null ? null : scope[id].id;

        var order = query.Order switch
        {
            "duration" => "coalesce(g.longest_seconds,0)::numeric",
            "rating" => "g.rating::numeric",
            "review_count" => "g.review_count::numeric",
            "random" =>
                "hashtextextended(g.show_uuid::text || '/' || g.song_id || '/' || @seed,0)::numeric",
            // Every row ties on score, so the tie-breakers alone order the page:
            // newest date, then show, then song ID.
            "date" => "0::numeric",
            _ => throw new ArgumentOutOfRangeException(nameof(query), query.Order, null),
        };
        var useShowScope =
            query.Years is not null || query.TourUuid is not null || query.VenueId is not null;
        var useSegueFilter = query.SeguedFrom is not null || query.SeguedInto is not null;
        var segueFilter = useSegueFilter
            ? $"JOIN LATERAL (SELECT 1 FROM ({NeighborsSql("t", "m")}) edge WHERE {SeguePredicate} LIMIT 1) matching_edge ON true"
            : "";
        var outputFilter = useSegueFilter ? $"WHERE {SeguePredicate}" : "";

        var rows = await session.Connection.QueryAsync<PerformanceRow>(
            session.Command(
                $"""
                WITH RECURSIVE song_map AS (
                  -- Every song ID of this artist mapped to the surviving song of its merges.
                  SELECT id, id AS canonical_id, ARRAY[id] AS path
                  FROM norm.songs WHERE artist_uuid=@artistUuid AND merged_into IS NULL
                  UNION ALL
                  SELECT s.id, m.canonical_id, m.path || s.id
                  FROM song_map m JOIN norm.songs s ON s.merged_into=m.id
                  WHERE s.artist_uuid=@artistUuid AND cardinality(m.path)<32 AND NOT s.id=ANY(m.path)
                ), {VenueMap}, show_scope AS MATERIALIZED (
                  SELECT * FROM ({ShowScopeSql}) scoped
                  WHERE @showBatch::uuid[] IS NULL OR scoped.uuid=ANY(@showBatch)
                ), eligible AS MATERIALIZED (
                  -- One row per playable file that matches the filters, with its show and source.
                  SELECT m.song_id, m.show_uuid, m.track_uuid, m.matching_part_ordinals,
                         sc.date, sc.display_date, sc.venue, s.id AS source_id, s.uuid AS source_uuid, s.is_soundboard,
                         s.avg_rating, coalesce(rc.source_review_count,0) AS review_count, t.duration
                  FROM norm.song_recording_matches(
                    @artistUuid, @songIds::text[], coalesce(@showBatch::uuid[], CASE WHEN @useShowScope THEN ARRAY(SELECT uuid FROM show_scope) END)
                  ) m
                  JOIN show_scope sc ON sc.uuid=m.show_uuid
                  JOIN sources s ON s.uuid=m.source_uuid
                  JOIN source_tracks t ON t.uuid=m.track_uuid
                  LEFT JOIN source_review_counts rc ON rc.source_id=s.id
                  {segueFilter}
                  WHERE (t.mp3_url IS NOT NULL OR t.flac_url IS NOT NULL)
                    AND NOT t.uuid=ANY(@ExcludedTracks)
                    AND (NOT @SoundboardOnly OR s.is_soundboard)
                    AND (@MinSeconds::double precision IS NULL OR t.duration>=@MinSeconds)
                    AND (@MaxSeconds::double precision IS NULL OR t.duration<=@MaxSeconds)
                ), source_counts AS (
                  -- Per performance: distinct eligible sources and their best rating and reviews.
                  SELECT song_id, show_uuid, count(*) AS source_count, bool_or(is_soundboard) AS soundboard,
                         max(avg_rating)::double precision AS rating, sum(review_count)::bigint AS review_count
                  FROM (SELECT DISTINCT song_id, show_uuid, source_id, is_soundboard, avg_rating, review_count FROM eligible) s
                  GROUP BY song_id, show_uuid
                ), tracks AS (
                  -- Per performance: its files, longest first; the first is the representative.
                  SELECT song_id, show_uuid, max(date) AS date, max(display_date) AS display_date, max(venue) AS venue, max(duration) AS longest_seconds,
                         array_agg(track_uuid ORDER BY duration DESC NULLS LAST, track_uuid) AS track_uuids
                  FROM eligible GROUP BY song_id, show_uuid
                ), grouped AS (
                  SELECT t.*, s.source_count, s.soundboard, coalesce(s.rating,0) AS rating, coalesce(s.review_count,0) AS review_count
                  FROM tracks t JOIN source_counts s USING (song_id,show_uuid)
                ), scored AS (
                  SELECT g.*, {order} AS score FROM grouped g
                ), page AS MATERIALIZED (
                  -- Rows strictly after the cursor tuple, in rank order.
                  SELECT * FROM scored
                  WHERE @afterScore::numeric IS NULL OR
                    (score,date,show_uuid,song_id) < (@afterScore,@afterDate::date,@afterShow,@afterSong)
                  ORDER BY score DESC,date DESC,show_uuid DESC,song_id DESC LIMIT @size
                )
                SELECT p.show_uuid AS ShowUuid, p.song_id AS SongId, song.name AS Song,
                       p.date::timestamp AS Date, p.display_date AS DisplayDate, p.venue, p.longest_seconds AS LongestSeconds, p.source_count AS SourceCount,
                       p.soundboard, p.rating, p.review_count AS ReviewCount, p.track_uuids AS TrackUuids,
                       p.score, m.source_uuid AS SourceUuid,
                       edge.previous_id AS PreviousId, previous.name AS PreviousSong, edge.segue_from AS SegueFrom,
                       edge.next_id AS NextId, following.name AS NextSong, edge.segue_into AS SegueInto
                FROM page p
                JOIN norm.songs song ON song.id=p.song_id
                JOIN eligible m ON m.show_uuid=p.show_uuid AND m.song_id=p.song_id AND m.track_uuid=p.track_uuids[1]
                JOIN source_tracks t ON t.uuid=m.track_uuid
                LEFT JOIN LATERAL (
                  SELECT * FROM ({NeighborsSql("t", "m")}) edge {outputFilter} LIMIT 1
                ) edge ON true
                LEFT JOIN norm.songs previous ON previous.id=edge.previous_id
                LEFT JOIN norm.songs following ON following.id=edge.next_id
                ORDER BY p.score DESC,p.date DESC,p.show_uuid DESC,p.song_id DESC
                """,
                new
                {
                    artistUuid,
                    artistId,
                    useShowScope,
                    query.Years,
                    query.TourUuid,
                    query.VenueId,
                    venueUuid = ImportedVenueUuid(query.VenueId),
                    showBatch,
                    songIds = CanonicalId(query.SongId) is { } id ? new[] { id } : null,
                    seguedFrom = CanonicalId(query.SeguedFrom),
                    seguedInto = CanonicalId(query.SeguedInto),
                    query.ExcludedShows,
                    query.ExcludedTracks,
                    query.SoundboardOnly,
                    query.MinSeconds,
                    query.MaxSeconds,
                    query.Seed,
                    size,
                    afterScore = query.After?.Score,
                    afterDate = query.After?.Date.ToDateTime(TimeOnly.MinValue),
                    afterShow = query.After?.Show,
                    afterSong = query.After?.Song,
                },
                cancellationToken
            )
        );
        return rows.ToArray();
    }

    // Resolve only the venues this artist's shows (and the requested venue) use,
    // following merges up to their surviving venue. Cycles and chains longer than
    // the public 32-node limit have no canonical venue.
    private const string VenueMap = """
        venue_chain AS (
          SELECT start.id, start.id AS current_id, ARRAY[start.id] AS path
          FROM (
            SELECT sv.venue_id AS id FROM shows sh JOIN norm.show_venues sv ON sv.show_uuid=sh.uuid
            WHERE sh.artist_id=@artistId
            UNION SELECT @VenueId::text WHERE @VenueId::text IS NOT NULL
          ) start
          UNION ALL
          SELECT c.id, v.merged_into, c.path || v.merged_into
          FROM venue_chain c JOIN norm.venues v ON v.id=c.current_id
          WHERE v.merged_into IS NOT NULL AND cardinality(c.path)<32 AND NOT v.merged_into=ANY(c.path)
        ), venue_map AS (
          SELECT c.id, c.current_id AS canonical_id
          FROM venue_chain c JOIN norm.venues v ON v.id=c.current_id
          WHERE v.merged_into IS NULL
        )
        """;

    private const string ShowScopeSql = """
        SELECT sh.id, sh.uuid, sh.date, sh.display_date, coalesce(v.name,iv.name) venue
        FROM shows sh
        LEFT JOIN tours tour ON tour.id=sh.tour_id
        LEFT JOIN venues iv ON iv.id=sh.venue_id
        LEFT JOIN norm.show_venues sv ON sv.show_uuid=sh.uuid
        LEFT JOIN venue_map vm ON vm.id=sv.venue_id
        LEFT JOIN norm.venues v ON v.id=vm.canonical_id
        LEFT JOIN venue_map filter_venue ON filter_venue.id=@VenueId
        LEFT JOIN norm.artist_venues av ON av.artist_uuid=@artistUuid AND av.venue_id=v.id
        WHERE sh.artist_id=@artistId AND NOT sh.uuid=ANY(@ExcludedShows)
          AND (@Years::int[] IS NULL OR extract(year from sh.date)::int=ANY(@Years))
          AND (@TourUuid::uuid IS NULL OR tour.uuid=@TourUuid)
          AND (@VenueId::text IS NULL OR v.id=filter_venue.canonical_id OR av.id=@VenueId OR iv.uuid=@venueUuid)
        """;

    private const string SeguePredicate = """
        (@seguedFrom::text IS NULL OR edge.previous_id=@seguedFrom AND edge.segue_from)
        AND (@seguedInto::text IS NULL OR edge.next_id=@seguedInto AND edge.segue_into)
        """;

    // Resolve adjacent parts first in the file, then over the immediately adjacent
    // file in the same set. Missing or unresolved parts never bridge a gap.
    private static string NeighborsSql(string track, string match) =>
        $"""
            SELECT prev_id.canonical_id AS previous_id, coalesce(before.segue_after,false) AS segue_from,
                   next_id.canonical_id AS next_id, current_part.segue_after AS segue_into
            FROM norm.track_songs current_part
            LEFT JOIN LATERAL (
              SELECT adjacent.uuid FROM source_tracks adjacent
              WHERE adjacent.source_set_id={track}.source_set_id AND adjacent.source_id={track}.source_id
                AND NOT adjacent.is_orphaned
                AND (adjacent.track_position,adjacent.uuid)<({track}.track_position,{track}.uuid)
              ORDER BY adjacent.track_position DESC,adjacent.uuid DESC LIMIT 1
            ) previous_file ON true
            LEFT JOIN LATERAL (
              SELECT adjacent.uuid FROM source_tracks adjacent
              WHERE adjacent.source_set_id={track}.source_set_id AND adjacent.source_id={track}.source_id
                AND NOT adjacent.is_orphaned
                AND (adjacent.track_position,adjacent.uuid)>({track}.track_position,{track}.uuid)
              ORDER BY adjacent.track_position,adjacent.uuid LIMIT 1
            ) next_file ON true
            LEFT JOIN LATERAL (
              SELECT part.song_id, part.resolved, part.segue_after FROM norm.track_songs part
              WHERE (part.track_uuid={track}.uuid AND part.part<current_part.part) OR part.track_uuid=previous_file.uuid
              ORDER BY (part.track_uuid={track}.uuid) DESC, part.part DESC LIMIT 1
            ) before ON true
            LEFT JOIN LATERAL (
              SELECT part.song_id, part.resolved FROM norm.track_songs part
              WHERE (part.track_uuid={track}.uuid AND part.part>current_part.part) OR part.track_uuid=next_file.uuid
              ORDER BY (part.track_uuid={track}.uuid) DESC, part.part LIMIT 1
            ) after ON true
            LEFT JOIN song_map prev_id ON prev_id.id=before.song_id AND before.resolved
            LEFT JOIN song_map next_id ON next_id.id=after.song_id AND after.resolved
            WHERE current_part.track_uuid={track}.uuid AND current_part.part=ANY({match}.matching_part_ordinals)
            ORDER BY current_part.part
            """;

    private static async Task<PerformancePopularity[]> ReadPopularity(
        CatalogReadSession session,
        PerformanceRow[] page,
        CancellationToken cancellationToken
    )
    {
        if (page.Length == 0)
        {
            return [];
        }

        var performances = await PopularityStore.ReadSelectedPerformances(
            session.Connection,
            page.Select(row => row.ShowUuid).ToArray(),
            page.Select(row => row.SongId).ToArray(),
            session.Transaction,
            cancellationToken
        );
        var sources = await PopularityStore.ReadRecordingMetrics(
            session.Connection,
            page.Select(row => row.SourceUuid).Distinct().ToArray(),
            session.Transaction,
            cancellationToken
        );
        return page.Select(row => new PerformancePopularity(
                performances.TryGetValue((row.ShowUuid, row.SongId), out var performance)
                    ? V4CatalogProjection.ProjectPopularity(performance)
                    : null,
                sources.TryGetValue(row.SourceUuid, out var source)
                    ? V4CatalogProjection.ProjectPopularity(source)
                    : null
            ))
            .ToArray();
    }

    private sealed record PerformancePopularity(
        PopularityResource? Performance,
        PopularityResource? Source
    );

    private static CatalogPerformance Project(
        PerformanceRow row,
        string slug,
        PerformancePopularity popularity
    ) =>
        new(
            new(row.ShowUuid),
            row.SongId,
            row.Song,
            DateOnly.FromDateTime(row.Date),
            row.Venue,
            row.SourceCount,
            row.Soundboard,
            row.Rating,
            row.ReviewCount,
            row.TrackUuids.Select(id => new TrackId(id)).ToArray(),
            new CatalogPerformanceRecording(
                new(row.TrackUuids[0]),
                new(row.SourceUuid),
                row.LongestSeconds,
                row.PreviousId is null
                    ? null
                    : new(row.PreviousId, row.PreviousSong!, row.SegueFrom),
                row.NextId is null ? null : new(row.NextId, row.NextSong!, row.SegueInto),
                CatalogWebLinks.Source(slug, row.DisplayDate, row.SourceUuid),
                popularity.Source
            ),
            popularity.Performance
        );

    private sealed record Artist(int Id, string Slug, bool Approved);

    private sealed class PerformanceRow
    {
        public Guid ShowUuid { get; init; }
        public string SongId { get; init; } = null!;
        public string Song { get; init; } = null!;
        public DateTime Date { get; init; }
        public string? Venue { get; init; }
        public int? LongestSeconds { get; init; }
        public long SourceCount { get; init; }
        public bool Soundboard { get; init; }
        public double Rating { get; init; }
        public long ReviewCount { get; init; }
        public Guid SourceUuid { get; init; }
        public string DisplayDate { get; init; } = null!;
        public string? PreviousId { get; init; }
        public string? PreviousSong { get; init; }
        public bool SegueFrom { get; init; }
        public string? NextId { get; init; }
        public string? NextSong { get; init; }
        public bool SegueInto { get; init; }
        public Guid[] TrackUuids { get; init; } = [];
        public decimal Score { get; init; }
    }
}

internal sealed class CatalogPerformanceTimeoutException : Exception;

[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public sealed record CatalogPerformancePage(
    IReadOnlyList<CatalogPerformance> Items,
    string? NextCursor
);

[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public sealed record CatalogPerformanceNeighbor(string Id, string Song, bool Segue);

/// <summary>Identifies a song performed at a show, independent of any recording.</summary>
[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public record CatalogPerformanceSummary(ShowId ShowId, string SongId, string Song);

[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public sealed record CatalogPerformance(
    ShowId ShowId,
    string SongId,
    string Song,
    DateOnly Date,
    string? Venue,
    long EligibleSourceCount,
    bool HasSoundboardSource,
    double MaxSourceRating,
    long EligibleSourceReviewCount,
    IReadOnlyList<TrackId> TrackIds,
    CatalogPerformanceRecording Representative,
    PopularityResource? Popularity
) : CatalogPerformanceSummary(ShowId, SongId, Song);

/// <summary>The longest eligible playable file, including any other songs in that file.</summary>
[JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
public sealed record CatalogPerformanceRecording(
    TrackId TrackId,
    SourceId SourceId,
    int? FileDurationSeconds,
    CatalogPerformanceNeighbor? Previous,
    CatalogPerformanceNeighbor? Next,
    string Link,
    PopularityResource? Popularity
);
