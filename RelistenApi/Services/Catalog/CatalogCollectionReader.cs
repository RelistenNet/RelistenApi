using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Relisten.Identifiers;

namespace Relisten.Catalog;

/// <summary>Selects bounded membership pages before hydration. Every input gets its own
/// SQL limit in the caller's snapshot, including nested lists for multiple parents.</summary>
public sealed class CatalogCollectionReader
{
    /// <summary>REST keeps complete collections using the same membership query, without a page loop.</summary>
    public async Task<CatalogPageSelection> ReadAll(
        CatalogReadSession session,
        CatalogCollection collection,
        CancellationToken token
    ) => (await ReadPages(session, [(collection, new CatalogPageRequest(int.MaxValue))], token))[0];

    public async Task<IReadOnlyList<CatalogPageSelection>> ReadPages(
        CatalogReadSession session,
        IReadOnlyList<(CatalogCollection Collection, CatalogPageRequest Page)> requests,
        CancellationToken cancellationToken
    )
    {
        if (requests.Count == 0)
        {
            return [];
        }
        var kind = requests[0].Collection.Kind;
        if (requests.Any(r => r.Collection.Kind != kind))
        {
            throw new ArgumentException("A collection batch must contain one entity kind.");
        }

        var results = new CatalogPageSelection[requests.Count];
        // Choose explicit ownership/filter predicates so PostgreSQL can use scoped indexes.
        // Values stay parameterized; original ordinals preserve order across SQL batches.
        var groups = requests
            .Select((request, index) => (request.Collection, request.Page, Index: index))
            .GroupBy(r => new
            {
                r.Collection.OwnerKind,
                Artist = r.Collection.Filters?.Artist is not null,
                Song = r.Collection.Filters?.Song is not null,
                Show = r.Collection.Filters?.ShowUuid is not null,
                Venue = r.Collection.Filters?.Venue is not null,
            });
        foreach (var group in groups)
        {
            var batch = group.ToArray();
            var pages = await ReadPageBatch(
                session,
                batch.Select(r => (r.Collection, r.Page)).ToArray(),
                cancellationToken
            );
            for (var i = 0; i < batch.Length; i++)
            {
                results[batch[i].Index] = pages[i];
            }
        }
        return results;
    }

    private static async Task<IReadOnlyList<CatalogPageSelection>> ReadPageBatch(
        CatalogReadSession session,
        IReadOnlyList<(CatalogCollection Collection, CatalogPageRequest Page)> requests,
        CancellationToken cancellationToken
    )
    {
        var collection = requests[0].Collection;
        var inputs = requests.Select((r, index) => Input(r.Collection, r.Page, index)).ToArray();
        var candidates = Candidates(collection);
        var matches = SongMatches(collection.Kind);
        // The lateral subquery applies cursor and limit independently for each input.
        // Both SQL fragments are server-owned; all caller data is in the JSON parameter.
        var sql = $$"""
            WITH requests AS (
              SELECT * FROM jsonb_to_recordset(@inputs::jsonb) AS r(
                ordinal int, owner text, owner_uuid uuid, owner_kind text, artist uuid, artists uuid[], date_from timestamp,
                date_through timestamp, venue text, song text, soundboard boolean, flac boolean,
                search text, include_auto boolean, include_fallback boolean, sort text, page_size int, has_after boolean,
                after_score float8, after_label text, after_id text, after_secondary float8, after_descending_label text, need_count boolean,
                month int,day int,recency text,recent_days int,require_sources boolean,show_uuid uuid,top_source_only boolean)
            )
            {{matches}}
            SELECT r.ordinal,p.* FROM requests r CROSS JOIN LATERAL (
              WITH candidates AS NOT MATERIALIZED ({{candidates}}),
              page AS (
                SELECT * FROM candidates c WHERE NOT r.has_after OR c.score<r.after_score
                  OR (c.score=r.after_score AND c.secondary<r.after_secondary)
                  OR (c.score=r.after_score AND c.secondary=r.after_secondary AND c.descending_label COLLATE "C"<r.after_descending_label COLLATE "C")
                  OR (c.score=r.after_score AND c.secondary=r.after_secondary AND c.descending_label=r.after_descending_label
                      AND (c.label COLLATE "C",c.key COLLATE "C")>(r.after_label COLLATE "C",r.after_id COLLATE "C"))
                ORDER BY c.score DESC,c.secondary DESC,c.descending_label COLLATE "C" DESC,c.label COLLATE "C",c.key COLLATE "C"
                LIMIT r.page_size::bigint+1
              )
              SELECT page.*,
                     CASE WHEN r.need_count THEN (SELECT count(*) FROM candidates) END total
              FROM page
              UNION ALL
              SELECT NULL,NULL,NULL,NULL,NULL,NULL,
                     CASE WHEN r.need_count THEN (SELECT count(*) FROM candidates) END
              WHERE NOT EXISTS (SELECT 1 FROM page)
            ) p ORDER BY r.ordinal,p.score DESC,p.secondary DESC,p.descending_label COLLATE "C" DESC,p.label COLLATE "C",p.key COLLATE "C"
            """;
        var rows = (
            await session.Connection.QueryAsync<PageRow>(
                session.Command(
                    sql,
                    new { inputs = JsonSerializer.Serialize(inputs) },
                    cancellationToken
                )
            )
        ).ToLookup(r => r.ordinal);
        return requests
            .Select(
                (request, index) =>
                {
                    var all = rows[index].Where(r => r.key is not null).ToArray();
                    var selected = all.Take(request.Page.First).ToArray();
                    return new CatalogPageSelection(
                        selected
                            .Select(r => Target(Enum.Parse<CatalogReferenceKind>(r.kind!), r.key!))
                            .ToArray(),
                        selected
                            .Select(r => new CatalogPageKey(
                                r.score,
                                r.label!,
                                r.key!,
                                r.secondary,
                                r.descending_label!
                            ))
                            .ToArray(),
                        all.Length > request.Page.First,
                        rows[index].FirstOrDefault()?.total
                    );
                }
            )
            .ToArray();
    }

    private static object Input(CatalogCollection collection, CatalogPageRequest page, int ordinal)
    {
        var f = collection.Filters ?? new();
        var owner = collection.Owner;
        if (
            owner is not null
            && collection.OwnerKind is { } ownerKind
            && ownerKind
                is not (
                    CatalogReferenceKind.Song
                    or CatalogReferenceKind.Venue
                    or CatalogReferenceKind.ArtistVenue
                )
        )
        {
            owner = UuidTypeId.Parse(owner, CatalogReferenceLookup.Prefix(ownerKind)).ToString("D");
        }
        return new
        {
            ordinal,
            owner,
            owner_uuid = Guid.TryParse(owner, out var ownerUuid) ? (Guid?)ownerUuid : null,
            owner_kind = collection.OwnerKind?.ToString(),
            artist = f.Artist,
            artists = f.Artists,
            date_from = f.From,
            date_through = f.Through,
            venue = f.Venue,
            song = f.Song,
            soundboard = f.Soundboard,
            flac = f.Flac,
            search = f.Search is { Length: >= 3 } term
                ? "%" + term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%"
                : null,
            show_uuid = f.ShowUuid,
            top_source_only = f.TopSourceOnly,
            month = f.Month,
            day = f.Day,
            recency = f.Recency?.ToString(),
            recent_days = f.RecentDays,
            require_sources = f.RequireSources,
            include_auto = f.IncludeAutoCreated,
            include_fallback = collection.IncludeFallback,
            sort = collection.Sort.ToString(),
            page_size = page.First,
            has_after = page.After is not null,
            after_score = page.After?.Score ?? 0,
            after_secondary = page.After?.Secondary ?? 0,
            after_descending_label = page.After?.DescendingLabel ?? "",
            after_label = page.After?.Label ?? "",
            after_id = page.After?.Id ?? "",
            need_count = page.Count,
        };
    }

    internal static CatalogResolvedTarget Target(CatalogReferenceKind kind, string key)
    {
        if (
            kind
            is CatalogReferenceKind.Song
                or CatalogReferenceKind.Venue
                or CatalogReferenceKind.ArtistVenue
        )
        {
            return new(kind, key, null);
        }
        var uuid = Guid.Parse(key);
        return new(kind, UuidTypeId.Format(CatalogReferenceLookup.Prefix(kind), uuid), uuid);
    }

    private static string Candidates(CatalogCollection collection)
    {
        var sql = BaseCandidates(collection);
        var kind = collection.Kind;
        // Search keeps its published tie-breaks; the same complete tuple is carried
        // in the keyset cursor. UUID remains the final stable identity tie-break.
        return kind switch
        {
            CatalogReferenceKind.Source => $$"""
                SELECT raw.key,raw.kind,
                  CASE WHEN r.sort='DateDescending' THEN (SELECT extract(epoch FROM sh.date) FROM shows sh WHERE sh.id=src.show_id)
                       WHEN r.sort='Updated' THEN extract(epoch FROM src.updated_at)
                       WHEN r.sort='Added' THEN extract(epoch FROM src.created_at)
                       ELSE COALESCE(src.avg_rating_weighted,-1e30) END::float8 score,
                  CASE WHEN r.search IS NULL THEN raw.label ELSE '' END label,
                  CASE WHEN r.search IS NULL THEN 0 ELSE COALESCE(src.duration,-1e30) END::float8 secondary,
                  CASE WHEN r.search IS NULL THEN '' ELSE src.display_date END descending_label
                FROM ({{sql}}) raw JOIN sources src ON src.uuid=raw.key::uuid
                """,
            CatalogReferenceKind.Show => $$"""
                SELECT raw.key,raw.kind,raw.score,
                  CASE WHEN r.search IS NULL AND r.sort='Default' THEN raw.label ELSE '' END label,
                  0::float8 secondary,
                  CASE WHEN r.search IS NOT NULL OR r.sort='DateDescending' THEN raw.label ELSE '' END descending_label
                FROM ({{sql}}) raw
                """,
            CatalogReferenceKind.Tour => $$"""
                SELECT raw.key,raw.kind,raw.score,CASE WHEN r.search IS NULL THEN '' ELSE raw.label END label,0::float8 secondary,
                  CASE WHEN r.search IS NULL THEN '' ELSE COALESCE(t.start_date::text,'~') END descending_label
                FROM ({{sql}}) raw JOIN tours t ON t.uuid=raw.key::uuid
                """,
            _ => $"SELECT raw.*,0::float8 secondary,''::text descending_label FROM ({sql}) raw",
        };
    }

    private static string BaseCandidates(CatalogCollection collection) =>
        collection.Kind switch
        {
            CatalogReferenceKind.Artist => """
                SELECT a.uuid::text key,'Artist' kind,
                  CASE WHEN r.sort='Popularity' THEN COALESCE(p.hot_score_30d,0)
                    WHEN r.search IS NOT NULL THEN (SELECT count(*) FROM shows sh WHERE sh.artist_id=a.id)
                    ELSE a.featured END::float8 score,a.name label
                FROM artists a LEFT JOIN artist_popularity p ON p.uuid=a.uuid
                WHERE (r.search IS NOT NULL OR NOT r.include_auto AND (a.featured & 2)=0
                  OR r.include_auto AND (EXISTS(SELECT 1 FROM shows sh WHERE sh.artist_id=a.id)
                                     OR EXISTS(SELECT 1 FROM sources src WHERE src.artist_id=a.id)))
                  AND (r.artist IS NULL OR a.uuid=r.artist)
                  AND (r.search IS NULL OR a.name ILIKE r.search)
                """,
            CatalogReferenceKind.Show => ShowCandidates(collection),
            CatalogReferenceKind.Source =>
                $$"""
                SELECT src.uuid::text key,'Source' kind,
                  COALESCE(src.avg_rating_weighted,0)::float8 score,src.uuid::text label
                FROM {{SourceMembership(collection)}}
                JOIN artists a ON a.id=src.artist_id JOIN shows sh ON sh.id=src.show_id
                LEFT JOIN features f ON f.artist_id=a.id
                WHERE {{ArtistPredicate(collection)}}
                  AND {{SourceOwnerPredicate(collection.OwnerKind)}}
                  AND (r.date_from IS NULL OR sh.date>=r.date_from)
                  AND (r.date_through IS NULL OR sh.date<=r.date_through)
                  AND (r.song IS NULL OR EXISTS(SELECT 1 FROM song_matches m WHERE m.requested_song=r.song AND m.source_uuid=src.uuid))
                  AND (r.venue IS NULL OR COALESCE(f.norm_catalogue,false) AND {{CatalogVenueReader.ShowMembershipSql(
                    "r.venue",
                    "sh.uuid"
                )}})
                  AND (r.soundboard IS NULL OR src.is_soundboard=r.soundboard)
                  AND (r.flac IS NULL OR (src.flac_type IN (1,2))=r.flac)
                  AND (r.search IS NULL OR src.upstream_identifier ILIKE r.search OR src.description ILIKE r.search
                    OR src.taper_notes ILIKE r.search OR src.source ILIKE r.search OR src.taper ILIKE r.search
                    OR src.transferrer ILIKE r.search OR src.lineage ILIKE r.search)
                """,
            CatalogReferenceKind.Track =>
                $$"""
                WITH ranked_sources AS MATERIALIZED (
                  SELECT candidate.uuid,row_number() OVER (
                    PARTITION BY candidate.show_id ORDER BY {{CatalogSourceRanking.OrderBy("candidate")}}
                  ) source_rank
                  FROM (
                    SELECT DISTINCT m.source_uuid FROM song_matches m
                    WHERE m.requested_song=COALESCE(CASE WHEN r.owner_kind='Song' THEN r.owner END,r.song)
                      AND (r.show_uuid IS NULL OR m.show_uuid=r.show_uuid)
                  ) matched
                  JOIN sources candidate ON candidate.uuid=matched.source_uuid
                  WHERE (r.owner_kind IS DISTINCT FROM 'Song' OR EXISTS(SELECT 1 FROM song_matches m WHERE m.requested_song=r.owner AND m.source_uuid=candidate.uuid))
                    AND (r.song IS NULL OR EXISTS(SELECT 1 FROM song_matches m WHERE m.requested_song=r.song AND m.source_uuid=candidate.uuid))
                    AND (r.soundboard IS NULL OR candidate.is_soundboard=r.soundboard)
                    AND (r.flac IS NULL OR (candidate.flac_type IN (1,2))=r.flac)
                )
                SELECT t.uuid::text key,'Track' kind,0::float8 score,
                  sh.display_date || ':' || sh.uuid || ':' ||
                  CASE WHEN r.show_uuid IS NOT NULL THEN COALESCE(lpad(ranked.source_rank::text,12,'0'),src.uuid::text) ELSE src.uuid::text END
                  || ':' || lpad(t.track_position::text,8,'0') label
                FROM {{TrackMembership(collection)}}
                JOIN sources src ON src.id=t.source_id
                JOIN source_sets ss ON ss.id=t.source_set_id AND ss.source_id=src.id
                JOIN artists a ON a.id=src.artist_id JOIN shows sh ON sh.id=src.show_id
                LEFT JOIN features f ON f.artist_id=a.id
                LEFT JOIN ranked_sources ranked ON ranked.uuid=src.uuid
                WHERE NOT t.is_orphaned AND {{ArtistPredicate(collection)}}
                  AND {{(collection.Filters?.ShowUuid is not null ? "sh.uuid=r.show_uuid" : "TRUE")}}
                  AND (NOT r.top_source_only OR ranked.source_rank=1)
                  AND {{TrackOwnerPredicate(collection.OwnerKind)}}
                  AND (r.song IS NULL OR COALESCE(f.norm_catalogue,false) AND EXISTS (
                      SELECT 1 FROM song_matches m WHERE m.requested_song=r.song AND m.track_uuid=t.uuid))
                  AND (r.date_from IS NULL OR sh.date>=r.date_from)
                  AND (r.date_through IS NULL OR sh.date<=r.date_through)
                  AND (r.venue IS NULL OR COALESCE(f.norm_catalogue,false) AND {{CatalogVenueReader.ShowMembershipSql(
                    "r.venue",
                    "sh.uuid"
                )}})
                  AND (r.search IS NULL OR t.title ILIKE r.search)
                  AND (r.soundboard IS NULL OR src.is_soundboard=r.soundboard)
                  AND (r.flac IS NULL OR (src.flac_type IN (1,2))=r.flac)
                """,
            CatalogReferenceKind.Year => """
                SELECT y.uuid::text key,'Year' kind,
                  CASE r.sort WHEN 'Rating' THEN COALESCE(y.avg_rating,-1e30)
                    WHEN 'Popularity' THEN COALESCE(p.hot_score_30d,0) ELSE 0 END::float8 score,y.year label
                FROM years y JOIN artists a ON a.id=y.artist_id
                LEFT JOIN year_popularity p ON p.uuid=y.uuid AND p.artist_uuid=a.uuid AND NOT p.is_global
                WHERE (r.artist IS NULL OR a.uuid=r.artist) AND (r.owner IS NULL OR a.uuid=r.owner_uuid)
                """,
            CatalogReferenceKind.Tour => """
                SELECT t.uuid::text key,'Tour' kind,
                CASE WHEN r.search IS NOT NULL THEN (SELECT count(*) FROM shows s WHERE s.tour_id=t.id)
                  WHEN r.sort='DateDescending' THEN COALESCE(extract(epoch FROM t.start_date),-1e30)
                  ELSE COALESCE(-extract(epoch FROM t.start_date),-1e30) END::float8 score,t.name label
                FROM tours t JOIN artists a ON a.id=t.artist_id
                WHERE (r.artist IS NULL OR a.uuid=r.artist) AND (r.owner IS NULL OR a.uuid=r.owner_uuid)
                  AND (r.search IS NULL OR t.name ILIKE r.search)
                """,
            CatalogReferenceKind.SourceReview => """
                SELECT rev.uuid::text key,'SourceReview' kind,
                  extract(epoch FROM rev.updated_at)::float8 score,rev.uuid::text label
                FROM source_reviews rev JOIN sources src ON src.id=rev.source_id
                WHERE src.uuid=r.owner_uuid AND (r.artist IS NULL OR src.artist_id=(SELECT id FROM artists WHERE uuid=r.artist))
                """,
            CatalogReferenceKind.ImportedSong => """
                SELECT s.uuid::text key,'ImportedSong' kind,0::float8 score,s.name label
                FROM setlist_songs s JOIN artists a ON a.id=s.artist_id
                WHERE (r.artist IS NULL OR a.uuid=r.artist) AND (r.owner IS NULL OR a.uuid=r.owner_uuid)
                  AND (r.search IS NULL OR s.name ILIKE r.search)
                """,
            CatalogReferenceKind.ImportedVenue => """
                SELECT v.uuid::text key,'ImportedVenue' kind,0::float8 score,COALESCE(v.name,'') label
                FROM venues v JOIN artists a ON a.id=v.artist_id
                WHERE (r.artist IS NULL OR a.uuid=r.artist) AND (r.owner IS NULL OR a.uuid=r.owner_uuid)
                  AND (r.search IS NULL OR v.name ILIKE r.search)
                """,
            CatalogReferenceKind.Song => CatalogBrowseMembership.Songs(collection),
            CatalogReferenceKind.Venue => """
                SELECT v.id key,'Venue' kind,0::float8 score,v.name label FROM norm.venues v
                WHERE v.merged_into IS NULL AND (r.search IS NULL OR v.name ILIKE r.search)
                  AND (r.artist IS NULL OR EXISTS(SELECT 1 FROM norm.artist_venues av
                      JOIN artists a ON a.uuid=av.artist_uuid JOIN features f ON f.artist_id=a.id AND f.norm_catalogue
                      WHERE av.venue_id=v.id AND av.artist_uuid=r.artist))
                """,
            CatalogReferenceKind.ArtistVenue => CatalogBrowseMembership.Venues(collection),
            _ => throw new ArgumentException(
                "This entity has no browse connection.",
                nameof(collection)
            ),
        };

    private static string ShowCandidates(CatalogCollection collection) =>
        $$"""
            SELECT s.uuid::text key,'Show' kind,
              CASE r.sort WHEN 'DateDescending' THEN 0
                 WHEN 'Updated' THEN COALESCE(extract(epoch FROM CASE WHEN r.recency='Added' THEN cnt.max_updated_at ELSE s.updated_at END),-1e30)
                 WHEN 'Added' THEN COALESCE(extract(epoch FROM cnt.max_created_at),0)
                 WHEN 'Rating' THEN COALESCE(cnt.max_avg_rating_weighted,-1e30)
                 WHEN 'Popularity' THEN COALESCE(p.hot_score_30d,0)
                 ELSE CASE WHEN r.search IS NULL THEN 0 ELSE COALESCE(cnt.source_count,0) END END::float8 score,
              s.display_date label
            FROM {{ShowMembership(collection)}}
            JOIN artists a ON a.id=s.artist_id
            LEFT JOIN features f ON f.artist_id=a.id
            LEFT JOIN show_source_information cnt ON cnt.show_id=s.id
            LEFT JOIN show_popularity p ON p.uuid=s.uuid AND NOT p.is_global AND p.artist_uuid=a.uuid
            WHERE {{ArtistPredicate(collection)}}
              AND (r.artists IS NULL OR a.uuid=ANY(r.artists))
              AND (r.month IS NULL OR extract(month FROM s.date)=COALESCE(NULLIF(r.month,0),extract(month FROM CURRENT_DATE)))
              AND (r.day IS NULL OR extract(day FROM s.date)=COALESCE(NULLIF(r.day,0),extract(day FROM CURRENT_DATE)))
              AND (NOT r.require_sources OR cnt.show_id IS NOT NULL)
              AND (r.recency IS NULL
                OR r.recency='Performed' AND (r.recent_days IS NULL OR s.date>CURRENT_DATE-LEAST(r.recent_days,90)*INTERVAL '1 day')
                OR r.recency='Updated' AND (r.recent_days IS NULL OR s.updated_at>CURRENT_DATE-LEAST(r.recent_days,90)*INTERVAL '1 day')
                OR r.recency='Added' AND (CASE WHEN r.sort='Updated' THEN cnt.max_updated_at ELSE cnt.max_created_at END) IS NOT NULL
                  AND (r.recent_days IS NULL OR (CASE WHEN r.sort='Updated' THEN cnt.max_updated_at ELSE cnt.max_created_at END)>=CURRENT_DATE-LEAST(r.recent_days,CURRENT_DATE-DATE '0001-01-01')*INTERVAL '1 day'))
              AND (r.date_from IS NULL OR s.date>=r.date_from)
              AND (r.date_through IS NULL OR s.date<=r.date_through)
              AND (r.soundboard IS NULL OR COALESCE(cnt.has_soundboard_source,false)=r.soundboard)
              AND (r.flac IS NULL OR COALESCE(cnt.has_flac,false)=r.flac)
              AND (r.search IS NULL OR s.display_date ILIKE r.search)
              AND (r.song IS NULL OR COALESCE(f.norm_catalogue,false) AND EXISTS (
                  SELECT 1 FROM song_matches m WHERE m.requested_song=r.song AND m.show_uuid=s.uuid))
              AND (r.venue IS NULL OR COALESCE(f.norm_catalogue,false) AND {{CatalogVenueReader.ShowMembershipSql(
                "r.venue"
            )}})
              AND {{(collection.OwnerKind is CatalogReferenceKind.Song or CatalogReferenceKind.Venue or CatalogReferenceKind.ArtistVenue ? "COALESCE(f.norm_catalogue,false)" : "TRUE")}}
            """;

    private static string ArtistPredicate(CatalogCollection collection) =>
        collection.Filters?.Artist is not null ? "a.uuid=r.artist" : "TRUE";

    private static string SourceOwnerPredicate(CatalogReferenceKind? owner) =>
        owner switch
        {
            CatalogReferenceKind.Artist => "a.uuid=r.owner_uuid",
            CatalogReferenceKind.Show => "sh.uuid=r.owner_uuid",
            _ => "r.owner IS NULL",
        };

    private static string TrackOwnerPredicate(CatalogReferenceKind? owner) =>
        owner switch
        {
            CatalogReferenceKind.Source => "src.uuid=r.owner_uuid",
            CatalogReferenceKind.Show => "sh.uuid=r.owner_uuid",
            CatalogReferenceKind.Song => """
                COALESCE(f.norm_catalogue,false) AND EXISTS (
                  SELECT 1 FROM song_matches m WHERE m.requested_song=r.owner AND m.track_uuid=t.uuid)
                """,
            _ => "r.owner IS NULL",
        };

    private static string SourceMembership(CatalogCollection collection) =>
        collection.Filters?.Song is not null
            ? """
                (SELECT DISTINCT m.source_uuid FROM song_matches m WHERE m.requested_song=r.song) matched
                JOIN sources src ON src.uuid=matched.source_uuid
                """
            : "sources src";

    // Ownership enters through a small membership set, before metadata and ordering.
    // Approval gates stay in ShowCandidates; the venue row definition is also used by REST.
    private static string ShowMembership(CatalogCollection collection) =>
        collection.OwnerKind switch
        {
            CatalogReferenceKind.Artist => """
                artists owner_artist JOIN shows s ON s.artist_id=owner_artist.id AND owner_artist.uuid=r.owner_uuid
                """,
            CatalogReferenceKind.Year => """
                years owner_year JOIN shows s ON s.year_id=owner_year.id AND owner_year.uuid=r.owner_uuid
                """,
            CatalogReferenceKind.Tour => """
                tours owner_tour JOIN shows s ON s.tour_id=owner_tour.id AND owner_tour.uuid=r.owner_uuid
                """,
            CatalogReferenceKind.Song => SongShowMembership("r.owner"),
            CatalogReferenceKind.Venue => VenueShowMembership("r.owner"),
            CatalogReferenceKind.ArtistVenue =>
                $$"""
                (SELECT av.artist_uuid,member.show_uuid FROM norm.artist_venues av
                 CROSS JOIN LATERAL ({{CatalogVenueReader.ShowMembershipRowsSql(
                    "av.venue_id"
                )}}) member
                 WHERE av.id=r.owner) owned
                JOIN shows s ON s.uuid=owned.show_uuid
                  AND s.artist_id=(SELECT id FROM artists WHERE uuid=owned.artist_uuid)
                """,
            CatalogReferenceKind.ImportedVenue => """
                venues owner_venue JOIN shows s ON s.venue_id=owner_venue.id AND owner_venue.uuid=r.owner_uuid
                """,
            CatalogReferenceKind.ImportedSong => """
                (SELECT DISTINCT ss.artist_id,ss.date FROM setlist_songs song
                 JOIN setlist_songs_plays sp ON sp.played_setlist_song_id=song.id
                 JOIN setlist_shows ss ON ss.id=sp.played_setlist_show_id
                 WHERE song.uuid=r.owner_uuid) owned
                JOIN shows s ON s.artist_id=owned.artist_id AND s.date=owned.date
                """,
            _ => collection.Filters switch
            {
                { Song: not null } => SongShowMembership("r.song"),
                { Venue: not null } => VenueShowMembership("r.venue"),
                { Artist: not null } =>
                    "artists owner_artist JOIN shows s ON s.artist_id=owner_artist.id AND owner_artist.uuid=r.artist",
                _ => "shows s",
            },
        };

    private static string SongShowMembership(string song) =>
        $$"""
            (SELECT DISTINCT m.show_uuid FROM song_matches m WHERE m.requested_song={{song}}) owned
            JOIN shows s ON s.uuid=owned.show_uuid
            """;

    private static string VenueShowMembership(string venue) =>
        $$"""
            ({{CatalogVenueReader.ShowMembershipRowsSql(venue)}}) owned
            JOIN shows s ON s.uuid=owned.show_uuid
            """;

    private static string TrackMembership(CatalogCollection collection) =>
        collection.OwnerKind == CatalogReferenceKind.Song || collection.Filters?.Song is not null
            ? """
                (SELECT DISTINCT m.track_uuid FROM song_matches m
                 WHERE m.requested_song=COALESCE(CASE WHEN r.owner_kind='Song' THEN r.owner END,r.song)
                   AND (r.show_uuid IS NULL OR m.show_uuid=r.show_uuid)) matched
                JOIN source_tracks t ON t.uuid=matched.track_uuid
                """
            : "source_tracks t";

    private static string SongMatches(CatalogReferenceKind kind) =>
        kind
            is CatalogReferenceKind.Show
                or CatalogReferenceKind.Source
                or CatalogReferenceKind.Track
            ? """
                , requested_songs AS MATERIALIZED (
                  SELECT DISTINCT song.id,song.artist_uuid
                  FROM requests r
                  CROSS JOIN LATERAL unnest(ARRAY[r.song,CASE WHEN r.owner_kind='Song' THEN r.owner END]) requested(id)
                  JOIN norm.songs song ON song.id=requested.id
                  JOIN artists a ON a.uuid=song.artist_uuid
                  JOIN features f ON f.artist_id=a.id AND f.norm_catalogue
                ), song_matches AS MATERIALIZED (
                  SELECT song.id requested_song,m.* FROM requested_songs song
                  CROSS JOIN LATERAL norm.song_recording_matches(song.artist_uuid,ARRAY[song.id]) m
                )
                """
            : "";

    private sealed class PageRow
    {
        public int ordinal { get; set; }
        public string? key { get; set; }
        public string? kind { get; set; }
        public double score { get; set; }
        public double secondary { get; set; }
        public string? descending_label { get; set; }
        public string? label { get; set; }
        public long? total { get; set; }
    }
}
