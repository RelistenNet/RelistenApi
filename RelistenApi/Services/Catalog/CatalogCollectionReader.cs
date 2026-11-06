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

        var inputs = requests.Select((r, index) => Input(r.Collection, r.Page, index)).ToArray();
        var candidates = Candidates(kind);
        // The lateral subquery applies cursor and limit independently for each input.
        // Both SQL fragments are server-owned; all caller data is in the JSON parameter.
        var sql = $$"""
            WITH requests AS (
              SELECT * FROM jsonb_to_recordset(@inputs::jsonb) AS r(
                ordinal int, owner text, owner_uuid uuid, owner_kind text, artist uuid, date_from timestamp,
                date_through timestamp, venue text, song text, soundboard boolean, flac boolean,
                search text, include_auto boolean, include_fallback boolean, sort text, page_size int, has_after boolean,
                after_score float8, after_label text, after_id text, need_count boolean,
                month int,day int,changed_since timestamp,changed_by_source boolean)
            )
            SELECT r.ordinal,p.* FROM requests r CROSS JOIN LATERAL (
              WITH candidates AS NOT MATERIALIZED ({{candidates}}),
              page AS (
                SELECT * FROM candidates c WHERE NOT r.has_after OR c.score<r.after_score
                  OR (c.score=r.after_score AND (c.label COLLATE "C",c.key COLLATE "C")>
                      (r.after_label COLLATE "C",r.after_id COLLATE "C"))
                ORDER BY c.score DESC,c.label COLLATE "C",c.key COLLATE "C"
                LIMIT r.page_size::bigint+1
              )
              SELECT page.*,
                     CASE WHEN r.need_count THEN (SELECT count(*) FROM candidates) END total
              FROM page
              UNION ALL
              SELECT NULL,NULL,NULL,NULL,
                     CASE WHEN r.need_count THEN (SELECT count(*) FROM candidates) END
              WHERE NOT EXISTS (SELECT 1 FROM page)
            ) p ORDER BY r.ordinal,p.score DESC,p.label COLLATE "C",p.key COLLATE "C"
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
                            .Select(r => new CatalogPageKey(r.score, r.label!, r.key!))
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
            date_from = f.From,
            date_through = f.Through,
            venue = f.Venue,
            song = f.Song,
            soundboard = f.Soundboard,
            flac = f.Flac,
            search = f.Search is { Length: >= 3 } term
                ? "%" + term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%"
                : null,
            month = f.Month,
            day = f.Day,
            changed_since = f.ChangedSince,
            changed_by_source = f.ChangedBySource,
            include_auto = f.IncludeAutoCreated,
            include_fallback = collection.IncludeFallback,
            sort = collection.Sort.ToString(),
            page_size = page.First,
            has_after = page.After is not null,
            after_score = page.After?.Score ?? 0,
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

    private static string Candidates(CatalogReferenceKind kind) =>
        kind switch
        {
            CatalogReferenceKind.Artist => """
                SELECT a.uuid::text key,'Artist' kind,0::float8 score,a.name label FROM artists a
                WHERE (r.include_auto OR (a.featured & 2)=0)
                  AND (r.artist IS NULL OR a.uuid=r.artist)
                  AND (r.search IS NULL OR a.name ILIKE r.search)
                """,
            CatalogReferenceKind.Show => ShowCandidates,
            CatalogReferenceKind.Source => """
                SELECT src.uuid::text key,'Source' kind,
                  COALESCE(src.avg_rating_weighted,0)::float8 score,src.uuid::text label
                FROM sources src JOIN artists a ON a.id=src.artist_id JOIN shows sh ON sh.id=src.show_id
                WHERE (r.artist IS NULL OR a.uuid=r.artist)
                  AND (r.owner IS NULL OR r.owner_kind='Artist' AND a.uuid=r.owner_uuid
                       OR r.owner_kind='Show' AND sh.uuid=r.owner_uuid)
                  AND (r.soundboard IS NULL OR src.is_soundboard=r.soundboard)
                  AND (r.flac IS NULL OR (src.flac_type IN (1,2))=r.flac)
                  AND (r.search IS NULL OR concat_ws(' ',src.upstream_identifier,src.description,
                       src.taper_notes,src.source,src.taper,src.transferrer,src.lineage) ILIKE r.search)
                """,
            CatalogReferenceKind.Track => """
                SELECT t.uuid::text key,'Track' kind,0::float8 score,
                  sh.display_date || ':' || src.uuid || ':' || lpad(t.track_position::text,8,'0') label
                FROM source_tracks t JOIN sources src ON src.id=t.source_id
                JOIN source_sets ss ON ss.id=t.source_set_id AND ss.source_id=src.id
                JOIN artists a ON a.id=src.artist_id JOIN shows sh ON sh.id=src.show_id
                LEFT JOIN features f ON f.artist_id=a.id
                WHERE NOT t.is_orphaned AND (r.artist IS NULL OR a.uuid=r.artist)
                  AND (r.owner IS NULL OR r.owner_kind='Source' AND src.uuid=r.owner_uuid
                    OR r.owner_kind='Show' AND sh.uuid=r.owner_uuid
                    OR r.owner_kind='Song' AND COALESCE(f.norm_catalogue,false) AND EXISTS (
                      SELECT 1 FROM norm.song_recording_matches(a.uuid,ARRAY[r.owner]) m WHERE m.track_uuid=t.uuid))
                  AND (r.song IS NULL OR COALESCE(f.norm_catalogue,false) AND EXISTS (
                      SELECT 1 FROM norm.song_recording_matches(a.uuid,ARRAY[r.song]) m WHERE m.track_uuid=t.uuid))
                  AND (r.soundboard IS NULL OR src.is_soundboard=r.soundboard)
                  AND (r.flac IS NULL OR (src.flac_type IN (1,2))=r.flac)
                """,
            CatalogReferenceKind.Year => """
                SELECT y.uuid::text key,'Year' kind,0::float8 score,y.year label
                FROM years y JOIN artists a ON a.id=y.artist_id
                WHERE (r.artist IS NULL OR a.uuid=r.artist) AND (r.owner IS NULL OR a.uuid=r.owner_uuid)
                """,
            CatalogReferenceKind.Tour => """
                SELECT t.uuid::text key,'Tour' kind,
                CASE WHEN r.search IS NULL THEN 0 ELSE (SELECT count(*) FROM shows s WHERE s.tour_id=t.id) END::float8 score,t.name label
                FROM tours t JOIN artists a ON a.id=t.artist_id
                WHERE (r.artist IS NULL OR a.uuid=r.artist) AND (r.owner IS NULL OR a.uuid=r.owner_uuid)
                  AND (r.search IS NULL OR t.name ILIKE r.search)
                """,
            CatalogReferenceKind.SourceReview => """
                SELECT rev.uuid::text key,'SourceReview' kind,
                  extract(epoch FROM rev.created_at)::float8 score,rev.uuid::text label
                FROM source_reviews rev JOIN sources src ON src.id=rev.source_id
                WHERE src.uuid=r.owner_uuid
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
            CatalogReferenceKind.Song => CatalogBrowseMembership.Songs,
            CatalogReferenceKind.Venue => """
                SELECT v.id key,'Venue' kind,0::float8 score,v.name label FROM norm.venues v
                WHERE v.merged_into IS NULL AND (r.search IS NULL OR v.name ILIKE r.search)
                """,
            CatalogReferenceKind.ArtistVenue => CatalogBrowseMembership.Venues,
            _ => throw new ArgumentException("This entity has no browse connection.", nameof(kind)),
        };

    private static string ShowCandidates =>
        $$"""
            SELECT s.uuid::text key,'Show' kind,
              CASE r.sort WHEN 'DateDescending' THEN extract(epoch FROM s.date)
                 WHEN 'Updated' THEN extract(epoch FROM CASE WHEN r.changed_by_source THEN cnt.max_updated_at ELSE s.updated_at END)
                 WHEN 'Added' THEN extract(epoch FROM cnt.max_created_at)
                 WHEN 'Rating' THEN COALESCE(cnt.max_avg_rating_weighted,0)
                 WHEN 'Popularity' THEN COALESCE(p.hot_score_30d,0)
                 ELSE CASE WHEN r.search IS NULL THEN 0 ELSE COALESCE(cnt.source_count,0) END END::float8 score,
              s.display_date label
            FROM shows s JOIN artists a ON a.id=s.artist_id
            LEFT JOIN features f ON f.artist_id=a.id
            LEFT JOIN show_source_information cnt ON cnt.show_id=s.id
            LEFT JOIN years y ON y.id=s.year_id LEFT JOIN tours t ON t.id=s.tour_id
            LEFT JOIN show_popularity p ON p.uuid=s.uuid AND NOT p.is_global AND p.artist_uuid=a.uuid
            WHERE (r.artist IS NULL OR a.uuid=r.artist)
              AND (r.month IS NULL OR extract(month FROM s.date)=COALESCE(NULLIF(r.month,0),extract(month FROM CURRENT_DATE)))
              AND (r.day IS NULL OR extract(day FROM s.date)=COALESCE(NULLIF(r.day,0),extract(day FROM CURRENT_DATE)))
              AND (r.changed_since IS NULL OR
                CASE WHEN r.changed_by_source AND r.sort='Updated' THEN cnt.max_updated_at
                      WHEN r.changed_by_source THEN cnt.max_created_at ELSE s.updated_at END>=r.changed_since)
              AND (r.date_from IS NULL OR s.date>=r.date_from)
              AND (r.date_through IS NULL OR s.date<=r.date_through)
              AND (r.soundboard IS NULL OR COALESCE(cnt.has_soundboard_source,false)=r.soundboard)
              AND (r.flac IS NULL OR COALESCE(cnt.has_flac,false)=r.flac)
              AND (r.search IS NULL OR s.display_date ILIKE r.search)
              AND (r.song IS NULL OR COALESCE(f.norm_catalogue,false) AND EXISTS (
                  SELECT 1 FROM norm.song_recording_matches(a.uuid,ARRAY[r.song]) m WHERE m.show_uuid=s.uuid))
              AND (r.venue IS NULL OR COALESCE(f.norm_catalogue,false) AND {{CatalogVenueReader.ShowMembershipSql(
                "r.venue"
            )}})
              AND (r.owner IS NULL
                OR r.owner_kind='Artist' AND a.uuid=r.owner_uuid
                OR r.owner_kind='Year' AND y.uuid=r.owner_uuid
                OR r.owner_kind='Tour' AND t.uuid=r.owner_uuid
                OR r.owner_kind='Song' AND COALESCE(f.norm_catalogue,false) AND EXISTS (
                  SELECT 1 FROM norm.song_recording_matches(a.uuid,ARRAY[r.owner]) m WHERE m.show_uuid=s.uuid)
                OR r.owner_kind='Venue' AND COALESCE(f.norm_catalogue,false) AND {{CatalogVenueReader.ShowMembershipSql(
                "r.owner"
            )}}
                OR r.owner_kind='ArtistVenue' AND COALESCE(f.norm_catalogue,false) AND EXISTS (
                  SELECT 1 FROM norm.artist_venues av
                  WHERE av.id=r.owner AND av.artist_uuid=a.uuid AND {{CatalogVenueReader.ShowMembershipSql(
                "av.venue_id"
            )}})
                OR r.owner_kind='ImportedVenue' AND EXISTS (
                  SELECT 1 FROM venues v WHERE v.id=s.venue_id AND v.uuid=r.owner_uuid)
                OR r.owner_kind='ImportedSong' AND EXISTS (
                  SELECT 1 FROM setlist_songs song JOIN setlist_songs_plays sp ON sp.played_setlist_song_id=song.id
                  JOIN setlist_shows ss ON ss.id=sp.played_setlist_show_id
                  WHERE song.uuid=r.owner_uuid AND ss.artist_id=s.artist_id AND ss.date=s.date))
            """;

    private sealed class PageRow
    {
        public int ordinal { get; set; }
        public string? key { get; set; }
        public string? kind { get; set; }
        public double score { get; set; }
        public string? label { get; set; }
        public long? total { get; set; }
    }
}
