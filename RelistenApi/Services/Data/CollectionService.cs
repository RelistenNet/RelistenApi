using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Relisten.Api.Models;
using Relisten.Import;
using Relisten.Services.Collections;
using Relisten.Services.Popularity;
using Relisten.Vendor.ArchiveOrg;

namespace Relisten.Data;

public class CollectionService : RelistenDataServiceBase, IArchiveCollectionResolverRepository,
    IAadamJacobsCollectionRepository
{
    private readonly ArtistService artistService;
    private readonly UpstreamSourceService upstreamSourceService;

    public CollectionService(DbService db, ArtistService artistService, UpstreamSourceService upstreamSourceService) :
        base(db)
    {
        this.artistService = artistService;
        this.upstreamSourceService = upstreamSourceService;
    }

    public async Task<ArchiveCollection> EnsureAadamJacobsCollection()
    {
        var upstreamSource = await upstreamSourceService.FindUpstreamSourceByName(ArchiveOrgImporter.DataSourceName);
        if (upstreamSource == null)
        {
            throw new InvalidOperationException("archive.org upstream source was not found.");
        }

        return await db.WithWriteConnection(con => con.QuerySingleAsync<ArchiveCollection>(@"
            INSERT INTO collections
                (
                    uuid,
                    slug,
                    upstream_source_id,
                    upstream_identifier,
                    collection_type,
                    name,
                    description
                )
            VALUES
                (
                    md5('root::collection::aadamjacobs')::uuid,
                    'aadam-jacobs',
                    @upstreamSourceId,
                    'aadamjacobs',
                    'taper_archive',
                    'Aadam Jacobs Collection',
                    'A curated Archive.org collection imported into playable Relisten sources.'
                )
            ON CONFLICT (slug) DO UPDATE SET
                upstream_source_id = EXCLUDED.upstream_source_id,
                upstream_identifier = EXCLUDED.upstream_identifier,
                collection_type = EXCLUDED.collection_type,
                name = EXCLUDED.name,
                description = EXCLUDED.description,
                updated_at = timezone('utc'::text, now())
            RETURNING *
        ", new {upstreamSourceId = upstreamSource.id}));
    }

    public Task<DateTime> GetServerTimestamp()
    {
        return db.WithConnection(con => con.QuerySingleAsync<DateTime>(@"
            SELECT timezone('utc'::text, now())
        "));
    }

    public Task UpsertIndexedItem(ArchiveCollection collection, ArchiveOrgCollectionIndexItem item,
        DateTime indexRunTimestamp)
    {
        return db.WithWriteConnection(con => con.ExecuteAsync(@"
            INSERT INTO collection_items
                (
                    collection_uuid,
                    upstream_identifier,
                    title,
                    creator_raw,
                    date_raw,
                    display_date,
                    year,
                    import_status,
                    last_seen_at,
                    updated_at
                )
            VALUES
                (
                    @collectionUuid,
                    @identifier,
                    @title,
                    @creator,
                    @date,
                    @date,
                    @year,
                    @pendingStatus,
                    @indexRunTimestamp,
                    timezone('utc'::text, now())
                )
            ON CONFLICT (collection_uuid, upstream_identifier) DO UPDATE SET
                title = EXCLUDED.title,
                creator_raw = EXCLUDED.creator_raw,
                date_raw = EXCLUDED.date_raw,
                display_date = EXCLUDED.display_date,
                year = EXCLUDED.year,
                last_seen_at = EXCLUDED.last_seen_at,
                removed_at = NULL,
                import_status = CASE
                    WHEN collection_items.source_uuid IS NULL THEN @pendingStatus
                    ELSE collection_items.import_status
                END,
                import_error = CASE
                    WHEN collection_items.source_uuid IS NULL THEN NULL
                    ELSE collection_items.import_error
                END,
                updated_at = EXCLUDED.updated_at
        ", new
        {
            collectionUuid = collection.uuid,
            item.identifier,
            title = item.title ?? item.identifier,
            creator = item.creator,
            date = item.date,
            item.year,
            pendingStatus = (int)ArchiveCollectionItemImportStatus.Pending,
            indexRunTimestamp
        }));
    }

    public Task<int> MarkMissingItemsRemoved(ArchiveCollection collection, DateTime indexRunTimestamp)
    {
        return db.WithWriteConnection(con => con.ExecuteAsync(@"
            UPDATE collection_items
            SET
                removed_at = @indexRunTimestamp,
                updated_at = timezone('utc'::text, now())
            WHERE collection_uuid = @collectionUuid
              AND removed_at IS NULL
              AND last_seen_at <> @indexRunTimestamp
        ", new {collectionUuid = collection.uuid, indexRunTimestamp}));
    }

    public Task CompleteIndexRun(ArchiveCollection collection, DateTime indexRunTimestamp, int activeItemCount)
    {
        return db.WithWriteConnection(con => con.ExecuteAsync(@"
            UPDATE collections
            SET
                item_count = @activeItemCount,
                indexed_at = @indexRunTimestamp,
                updated_at = timezone('utc'::text, now())
            WHERE uuid = @collectionUuid
        ", new {collectionUuid = collection.uuid, indexRunTimestamp, activeItemCount}));
    }

    public async Task<IReadOnlyList<CollectionItem>> LoadActivePendingItems(ArchiveCollection collection)
    {
        var items = await db.WithConnection(con => con.QueryAsync<CollectionItem>(@"
            SELECT *
            FROM collection_items
            WHERE collection_uuid = @collectionUuid
              AND removed_at IS NULL
              AND NOT (
                  import_status IN (@linkedStatus, @importedStatus)
                  AND artist_uuid IS NOT NULL
                  AND source_uuid IS NOT NULL
                  AND show_uuid IS NOT NULL
              )
            ORDER BY upstream_identifier
        ", new
        {
            collectionUuid = collection.uuid,
            linkedStatus = (int)ArchiveCollectionItemImportStatus.LinkedExistingSource,
            importedStatus = (int)ArchiveCollectionItemImportStatus.ImportedSource
        }));

        return items.ToList();
    }

    public Task<Guid?> FindExistingSourceUuid(int artistId, string upstreamIdentifier)
    {
        return db.WithConnection(con => con.QuerySingleOrDefaultAsync<Guid?>(@"
            SELECT uuid
            FROM sources
            WHERE artist_id = @artistId
              AND upstream_identifier = @upstreamIdentifier
            LIMIT 1
        ", new {artistId, upstreamIdentifier}));
    }

    public Task MarkItemLinkedToSource(ArchiveCollection collection, string upstreamIdentifier, Artist artist,
        Guid sourceUuid, ArchiveCollectionItemImportStatus status)
    {
        return db.WithWriteConnection(con => con.ExecuteAsync(@"
            UPDATE collection_items
            SET
                artist_uuid = @artistUuid,
                source_uuid = @sourceUuid,
                import_status = @status,
                import_error = NULL,
                last_imported_at = timezone('utc'::text, now()),
                updated_at = timezone('utc'::text, now())
            WHERE collection_uuid = @collectionUuid
              AND upstream_identifier = @upstreamIdentifier
        ", new
        {
            collectionUuid = collection.uuid,
            upstreamIdentifier,
            artistUuid = artist.uuid,
            sourceUuid,
            status = (int)status
        }));
    }

    public Task MarkItemSkipped(ArchiveCollection collection, string upstreamIdentifier, string reason)
    {
        return UpdateItemStatus(collection, upstreamIdentifier, ArchiveCollectionItemImportStatus.Skipped, reason);
    }

    public Task MarkItemImportError(ArchiveCollection collection, string upstreamIdentifier, string error)
    {
        return UpdateItemStatus(collection, upstreamIdentifier, ArchiveCollectionItemImportStatus.ImportError, error);
    }

    public Task WithArtistImportLock(int artistId, Func<Task> action)
    {
        return db.WithAdvisoryLock(DbService.ArtistImportLockKey(artistId), action);
    }

    private Task UpdateItemStatus(ArchiveCollection collection, string upstreamIdentifier,
        ArchiveCollectionItemImportStatus status, string message)
    {
        return db.WithWriteConnection(con => con.ExecuteAsync(@"
            UPDATE collection_items
            SET
                import_status = @status,
                import_error = @message,
                updated_at = timezone('utc'::text, now())
            WHERE collection_uuid = @collectionUuid
              AND upstream_identifier = @upstreamIdentifier
        ", new
        {
            collectionUuid = collection.uuid,
            upstreamIdentifier,
            status = (int)status,
            message
        }));
    }

    public async Task LinkImportedItemsForArtist(ArchiveCollection collection, int artistId)
    {
        await db.WithWriteConnection(con => con.ExecuteAsync(@"
            UPDATE collection_items ci
            SET
                artist_uuid = a.uuid,
                source_uuid = s.uuid,
                show_uuid = sh.uuid,
                last_imported_at = COALESCE(ci.last_imported_at, timezone('utc'::text, now())),
                updated_at = timezone('utc'::text, now())
            FROM sources s
            JOIN artists a ON a.id = s.artist_id
            LEFT JOIN shows sh ON sh.id = s.show_id
            WHERE ci.collection_uuid = @collectionUuid
              AND ci.removed_at IS NULL
              AND ci.upstream_identifier = s.upstream_identifier
              AND s.artist_id = @artistId
        ", new {collectionUuid = collection.uuid, artistId}));

        await artistService.TouchApiUpdatedAt(artistId);
    }

    public Task RecomputeCollectionYears(ArchiveCollection collection)
    {
        return db.WithWriteConnection(con => con.ExecuteAsync(@"
            DELETE FROM collection_years
            WHERE collection_uuid = @collectionUuid;

            INSERT INTO collection_years
                (
                    collection_uuid,
                    uuid,
                    year,
                    artist_count,
                    show_count,
                    source_count,
                    duration,
                    avg_duration,
                    avg_rating,
                    updated_at
                )
            SELECT
                ci.collection_uuid,
                md5(ci.collection_uuid::text || '::collection_year::' || COALESCE(ci.year::text, to_char(sh.date, 'YYYY')))::uuid,
                COALESCE(ci.year::text, to_char(sh.date, 'YYYY')) AS year,
                COUNT(DISTINCT ci.artist_uuid) AS artist_count,
                COUNT(DISTINCT ci.show_uuid) AS show_count,
                COUNT(DISTINCT ci.source_uuid) AS source_count,
                COALESCE(SUM(s.duration), 0)::bigint AS duration,
                AVG(sh.avg_duration) AS avg_duration,
                AVG(sh.avg_rating) AS avg_rating,
                timezone('utc'::text, now()) AS updated_at
            FROM collection_items ci
            JOIN sources s ON s.uuid = ci.source_uuid
            JOIN shows sh ON sh.uuid = ci.show_uuid
            WHERE ci.collection_uuid = @collectionUuid
              AND ci.removed_at IS NULL
              AND ci.source_uuid IS NOT NULL
            GROUP BY ci.collection_uuid, COALESCE(ci.year::text, to_char(sh.date, 'YYYY'))
        ", new {collectionUuid = collection.uuid}));
    }

    public Task CompleteImportRun(ArchiveCollection collection, DateTime importRunTimestamp)
    {
        return db.WithWriteConnection(con => con.ExecuteAsync(@"
            UPDATE collections
            SET
                last_imported_at = @importRunTimestamp,
                updated_at = timezone('utc'::text, now())
            WHERE uuid = @collectionUuid
        ", new {collectionUuid = collection.uuid, importRunTimestamp}));
    }

    public static bool IsValidMonthDay(int month, int day)
    {
        if (month is < 1 or > 12)
        {
            return false;
        }

        return day >= 1 && day <= DateTime.DaysInMonth(2024, month);
    }

    public async Task<IReadOnlyList<CollectionSummary>> AllCollections()
    {
        var collections = await db.WithConnection(con => con.QueryAsync<CollectionSummary>(@"
            SELECT
                c.uuid,
                c.slug,
                c.upstream_identifier,
                c.name,
                c.item_count,
                COUNT(DISTINCT ci.artist_uuid) FILTER (WHERE ci.removed_at IS NULL AND ci.artist_uuid IS NOT NULL) AS artist_count,
                COUNT(DISTINCT ci.show_uuid) FILTER (WHERE ci.removed_at IS NULL AND ci.show_uuid IS NOT NULL) AS show_count,
                COUNT(DISTINCT ci.source_uuid) FILTER (WHERE ci.removed_at IS NULL AND ci.source_uuid IS NOT NULL) AS source_count,
                c.indexed_at
            FROM collections c
            LEFT JOIN collection_items ci ON ci.collection_uuid = c.uuid
            GROUP BY c.uuid
            ORDER BY c.name
        "));

        return collections.ToList();
    }

    public Task<CollectionDetail?> FindCollection(string collectionUuidOrSlug)
    {
        var isUuid = Guid.TryParse(collectionUuidOrSlug, out var collectionUuid);
        return db.WithConnection(con => con.QuerySingleOrDefaultAsync<CollectionDetail>(@"
            SELECT
                c.uuid,
                c.slug,
                c.upstream_identifier,
                c.name,
                c.description,
                c.item_count,
                COUNT(DISTINCT ci.artist_uuid) FILTER (WHERE ci.removed_at IS NULL AND ci.artist_uuid IS NOT NULL) AS artist_count,
                COUNT(DISTINCT ci.show_uuid) FILTER (WHERE ci.removed_at IS NULL AND ci.show_uuid IS NOT NULL) AS show_count,
                COUNT(DISTINCT ci.source_uuid) FILTER (WHERE ci.removed_at IS NULL AND ci.source_uuid IS NOT NULL) AS source_count,
                c.indexed_at
            FROM collections c
            LEFT JOIN collection_items ci ON ci.collection_uuid = c.uuid
            WHERE (@isUuid = true AND c.uuid = @collectionUuid)
               OR (@isUuid = false AND c.slug = @collectionUuidOrSlug)
            GROUP BY c.uuid
        ", new {isUuid, collectionUuid, collectionUuidOrSlug}));
    }

    public async Task<IReadOnlyList<ArtistWithCounts>> ArtistsForCollection(CollectionDetail collection)
    {
        var artists = await db.WithConnection(con => con.QueryAsync<ArtistWithCounts, Features, ArtistWithCounts>(@"
            SELECT
                a.*,
                COUNT(DISTINCT ci.show_uuid) AS show_count,
                COUNT(DISTINCT ci.source_uuid) AS source_count,
                f.*
            FROM collection_items ci
            JOIN artists a ON a.uuid = ci.artist_uuid
            LEFT JOIN features f ON f.artist_id = a.id
            WHERE ci.collection_uuid = @collectionUuid
              AND ci.removed_at IS NULL
              AND ci.source_uuid IS NOT NULL
            GROUP BY a.id, f.id
            ORDER BY a.sort_name, a.name
        ", (artist, features) =>
        {
            artist.features = features;
            artist.upstream_sources = Array.Empty<ArtistUpstreamSource>();
            return artist;
        }, new {collectionUuid = collection.uuid}));

        return artists.ToList();
    }

    public async Task<IReadOnlyList<CollectionYear>> YearsForCollection(CollectionDetail collection)
    {
        var years = await db.WithConnection(con => con.QueryAsync<CollectionYear>(@"
            SELECT *
            FROM collection_years
            WHERE collection_uuid = @collectionUuid
            ORDER BY year
        ", new {collectionUuid = collection.uuid}));

        return years.ToList();
    }

    public async Task<CollectionYearWithShows?> YearWithShowsForCollection(CollectionDetail collection,
        string yearUuidOrYear)
    {
        var isUuid = Guid.TryParse(yearUuidOrYear, out var yearUuid);
        var year = await db.WithConnection(con => con.QuerySingleOrDefaultAsync<CollectionYearWithShows>(@"
            SELECT *
            FROM collection_years
            WHERE collection_uuid = @collectionUuid
              AND ((@isUuid = true AND uuid = @yearUuid) OR (@isUuid = false AND year = @yearUuidOrYear))
        ", new {collectionUuid = collection.uuid, isUuid, yearUuid, yearUuidOrYear}));

        if (year == null)
        {
            return null;
        }

        year.shows = (await QueryCollectionShows(collection.uuid, @"
            EXISTS (
                SELECT 1
                FROM collection_items ciw
                WHERE ciw.collection_uuid = @collectionUuid
                  AND ciw.removed_at IS NULL
                  AND ciw.show_uuid = sh.uuid
                  AND COALESCE(ciw.year::text, to_char(sh.date, 'YYYY')) = @year
            )
        ", new {collectionUuid = collection.uuid, year = year.year}, "sh.display_date ASC")).ToList();

        return year;
    }

    public async Task<IReadOnlyList<Show>> OnThisDayForCollection(CollectionDetail collection, int month, int day)
    {
        return (await QueryCollectionShows(collection.uuid, @"
            EXTRACT(month from sh.date) = @month
            AND EXTRACT(day from sh.date) = @day
        ", new {collectionUuid = collection.uuid, month, day}, "sh.display_date ASC")).ToList();
    }

    public async Task<CollectionPopularTrendingShowsResponse> PopularTrendingShowsForCollection(
        CollectionDetail collection, int limit, PopularitySortWindow sortWindow)
    {
        var rows = (await db.WithConnection(con => con.QueryAsync<CollectionShowPopularityRow>(@"
            WITH collection_sources AS (
                SELECT DISTINCT source_uuid
                FROM collection_items
                WHERE collection_uuid = @collectionUuid
                  AND removed_at IS NULL
                  AND source_uuid IS NOT NULL
            ),
            plays_90d AS (
                SELECT p.show_uuid, SUM(p.plays) AS plays_90d
                FROM source_track_plays_daily p
                JOIN collection_sources cs ON cs.source_uuid = p.source_uuid
                WHERE p.play_day >= now() - interval '90 days'
                GROUP BY 1
            ),
            plays_30d AS (
                SELECT p.show_uuid, SUM(p.plays) AS plays_30d,
                    SUM(p.total_track_seconds)::bigint AS seconds_30d
                FROM source_track_plays_daily p
                JOIN collection_sources cs ON cs.source_uuid = p.source_uuid
                WHERE p.play_day >= now() - interval '30 days'
                GROUP BY 1
            ),
            plays_7d AS (
                SELECT p.show_uuid, SUM(p.plays) AS plays_7d,
                    SUM(p.total_track_seconds)::bigint AS seconds_7d
                FROM source_track_plays_daily p
                JOIN collection_sources cs ON cs.source_uuid = p.source_uuid
                WHERE p.play_day >= now() - interval '7 days'
                GROUP BY 1
            ),
            plays_6h AS (
                SELECT p.show_uuid, SUM(p.plays) AS plays_6h
                FROM source_track_plays_hourly p
                JOIN collection_sources cs ON cs.source_uuid = p.source_uuid
                WHERE p.play_hour >= now() - interval '6 hours'
                GROUP BY 1
            ),
            plays_48h AS (
                SELECT p.show_uuid, SUM(p.plays) AS plays_48h,
                    SUM(p.total_track_seconds)::bigint AS seconds_48h
                FROM source_track_plays_hourly p
                JOIN collection_sources cs ON cs.source_uuid = p.source_uuid
                WHERE p.play_hour >= now() - interval '48 hours'
                GROUP BY 1
            )
            SELECT
                COALESCE(p30.show_uuid, p48.show_uuid, p7.show_uuid, p90.show_uuid, p6.show_uuid) AS show_uuid,
                COALESCE(p30.plays_30d, 0) AS plays_30d,
                COALESCE(p7.plays_7d, 0) AS plays_7d,
                COALESCE(p48.plays_48h, 0) AS plays_48h,
                COALESCE(p90.plays_90d, 0) AS plays_90d,
                COALESCE(p6.plays_6h, 0) AS plays_6h,
                COALESCE(p30.seconds_30d, 0) AS seconds_30d,
                COALESCE(p7.seconds_7d, 0) AS seconds_7d,
                COALESCE(p48.seconds_48h, 0) AS seconds_48h
            FROM plays_30d p30
            FULL OUTER JOIN plays_48h p48 ON p48.show_uuid = p30.show_uuid
            FULL OUTER JOIN plays_7d p7 ON p7.show_uuid = COALESCE(p30.show_uuid, p48.show_uuid)
            FULL OUTER JOIN plays_90d p90 ON p90.show_uuid = COALESCE(p30.show_uuid, p48.show_uuid, p7.show_uuid)
            FULL OUTER JOIN plays_6h p6 ON p6.show_uuid = COALESCE(p30.show_uuid, p48.show_uuid, p7.show_uuid, p90.show_uuid)
        ", new {collectionUuid = collection.uuid}))).ToList();

        var shows = (await QueryCollectionShows(collection.uuid, "sh.uuid = ANY(@showUuids)",
                new {collectionUuid = collection.uuid, showUuids = rows.Select(r => r.show_uuid).ToArray()},
                "sh.display_date ASC"))
            .ToDictionary(show => show.uuid);

        var candidates = rows
            .Where(row => shows.ContainsKey(row.show_uuid))
            .Select(row =>
            {
                var show = shows[row.show_uuid];
                show.popularity = PopularityService.CreateMetrics(row.plays_30d, row.plays_7d, row.plays_6h,
                    row.plays_48h, row.plays_90d, row.seconds_30d, row.seconds_7d, row.seconds_48h);
                return show;
            })
            .ToList();

        return new CollectionPopularTrendingShowsResponse
        {
            collection_uuid = collection.uuid,
            collection_slug = collection.slug,
            collection_name = collection.name,
            popular_shows = candidates
                .OrderByDescending(show => GetSortWindowHotScore(show, sortWindow))
                .ThenByDescending(show => GetSortWindowPlays(show, sortWindow))
                .Take(limit)
                .ToList(),
            trending_shows = candidates
                .OrderByDescending(show => show.popularity?.trend_ratio ?? 0)
                .ThenByDescending(show => show.popularity?.windows.hours_48h.plays ?? 0)
                .Take(limit)
                .ToList()
        };
    }

    private async Task<IEnumerable<Show>> QueryCollectionShows(Guid collectionUuid, string where, object parameters,
        string orderBy, int? limit = null)
    {
        var limitClause = limit == null ? "" : $"LIMIT {limit}";
        var shows = await db.WithConnection(con => con.QueryAsync<Show, VenueWithShowCount, Tour, Era, Year, Show>($@"
            WITH collection_show_info AS (
                SELECT
                    ci.show_uuid,
                    MAX(s.updated_at) AS max_updated_at,
                    COUNT(DISTINCT ci.source_uuid) AS source_count,
                    BOOL_OR(s.is_soundboard) AS has_soundboard_source,
                    BOOL_OR(s.flac_type = 1 OR s.flac_type = 2) AS has_flac
                FROM collection_items ci
                JOIN sources s ON s.uuid = ci.source_uuid
                WHERE ci.collection_uuid = @collectionUuid
                  AND ci.removed_at IS NULL
                  AND ci.show_uuid IS NOT NULL
                  AND ci.source_uuid IS NOT NULL
                GROUP BY ci.show_uuid
            )
            SELECT
                sh.*,
                a.uuid as artist_uuid,
                csi.max_updated_at as most_recent_source_updated_at,
                csi.source_count,
                csi.has_soundboard_source,
                csi.has_flac as has_streamable_flac_source,
                v.uuid as venue_uuid,
                t.uuid as tour_uuid,
                y.uuid as year_uuid,
                v.*,
                a.uuid as artist_uuid,
                COALESCE(venue_counts.shows_at_venue, 0) as shows_at_venue,
                t.*,
                a.uuid as artist_uuid,
                e.*,
                a.uuid as artist_uuid,
                y.*,
                a.uuid as artist_uuid
            FROM collection_show_info csi
            JOIN shows sh ON sh.uuid = csi.show_uuid
            JOIN artists a ON sh.artist_id = a.id
            LEFT JOIN venues v ON sh.venue_id = v.id
            LEFT JOIN tours t ON sh.tour_id = t.id
            LEFT JOIN eras e ON sh.era_id = e.id
            LEFT JOIN years y ON sh.year_id = y.id
            LEFT JOIN venue_show_counts venue_counts ON venue_counts.id = sh.venue_id
            WHERE {where}
            ORDER BY {orderBy}
            {limitClause}
        ", (show, venue, tour, era, year) =>
        {
            show.venue = venue;
            show.tour = tour;
            show.era = era;
            show.year = year;
            return show;
        }, parameters));

        return shows;
    }

    private static double GetSortWindowHotScore(Show show, PopularitySortWindow sortWindow)
    {
        var windows = show.popularity?.windows;
        if (windows == null)
        {
            return 0;
        }

        return sortWindow switch
        {
            PopularitySortWindow.Hours48 => windows.hours_48h.hot_score,
            PopularitySortWindow.Days7 => windows.days_7d.hot_score,
            _ => windows.days_30d.hot_score
        };
    }

    private static long GetSortWindowPlays(Show show, PopularitySortWindow sortWindow)
    {
        var windows = show.popularity?.windows;
        if (windows == null)
        {
            return 0;
        }

        return sortWindow switch
        {
            PopularitySortWindow.Hours48 => windows.hours_48h.plays,
            PopularitySortWindow.Days7 => windows.days_7d.plays,
            _ => windows.days_30d.plays
        };
    }

    private sealed class CollectionShowPopularityRow
    {
        public Guid show_uuid { get; set; }
        public long plays_30d { get; set; }
        public long plays_7d { get; set; }
        public long plays_48h { get; set; }
        public long plays_6h { get; set; }
        public long plays_90d { get; set; }
        public long seconds_30d { get; set; }
        public long seconds_7d { get; set; }
        public long seconds_48h { get; set; }
    }

    public Task<CollectionArtistMapping?> FindMapping(Guid collectionUuid, string creatorName)
    {
        return db.WithConnection(con => con.QuerySingleOrDefaultAsync<CollectionArtistMapping>(@"
            SELECT *
            FROM collection_artist_mappings
            WHERE collection_uuid = @collectionUuid
              AND creator_name = @creatorName
        ", new {collectionUuid, creatorName}));
    }

    public Task<Artist?> FindArtistByUuid(Guid artistUuid)
    {
        return artistService.FindArtistByUuid(artistUuid);
    }

    public async Task<IReadOnlyList<Artist>> FindArtistsBySourceIdentifier(string upstreamIdentifier)
    {
        var artists = await QueryArtists(@"
            JOIN sources s ON s.artist_id = a.id
            WHERE s.upstream_identifier = @upstreamIdentifier
        ", new {upstreamIdentifier});

        return artists.ToList();
    }

    public async Task<Artist?> FindArtistByExactName(string name)
    {
        var artists = await QueryArtists(@"
            WHERE a.name = @name
            LIMIT 1
        ", new {name});

        return artists.FirstOrDefault();
    }

    public async Task<Artist?> FindArtistByNormalizedName(string normalizedName)
    {
        var artists = await QueryArtists(@"
            WHERE regexp_replace(lower(a.name), '[^a-z0-9]+', '', 'g') = @normalizedName
            ORDER BY a.featured DESC, a.name
            LIMIT 1
        ", new {normalizedName});

        return artists.FirstOrDefault();
    }

    public async Task<IReadOnlyList<string>> LoadArtistSlugs()
    {
        var slugs = await db.WithConnection(con => con.QueryAsync<string>(@"
            SELECT slug
            FROM artists
        "));

        return slugs.ToList();
    }

    public Task<SlimArtistWithFeatures> SaveArtist(SlimArtistWithFeatures artist)
    {
        return artistService.Save(artist);
    }

    public Task SaveMapping(CollectionArtistMapping mapping)
    {
        return db.WithWriteConnection(con => con.ExecuteAsync(@"
            INSERT INTO collection_artist_mappings
                (
                    collection_uuid,
                    creator_name,
                    artist_uuid,
                    canonical_name,
                    blocked,
                    block_reason,
                    decision_source,
                    updated_at
                )
            VALUES
                (
                    @collection_uuid,
                    @creator_name,
                    @artist_uuid,
                    @canonical_name,
                    @blocked,
                    @block_reason,
                    @decision_source,
                    timezone('utc'::text, now())
                )
            ON CONFLICT (collection_uuid, creator_name) DO UPDATE SET
                artist_uuid = EXCLUDED.artist_uuid,
                canonical_name = EXCLUDED.canonical_name,
                blocked = EXCLUDED.blocked,
                block_reason = EXCLUDED.block_reason,
                decision_source = EXCLUDED.decision_source,
                updated_at = EXCLUDED.updated_at
        ", mapping));
    }

    public Task TouchApiUpdatedAt(int artistId)
    {
        return artistService.TouchApiUpdatedAt(artistId);
    }

    private async Task<IEnumerable<Artist>> QueryArtists(string fromAndWhere, object parameters)
    {
        var artists = await db.WithConnection(con => con.QueryAsync<Artist, Features, Artist>($@"
            SELECT DISTINCT
                a.*, f.*
            FROM artists a
            LEFT JOIN features f ON f.artist_id = a.id
            {fromAndWhere}
        ", (artist, features) =>
        {
            artist.features = features;
            artist.upstream_sources = Array.Empty<ArtistUpstreamSource>();
            return artist;
        }, parameters));

        return artists;
    }
}
