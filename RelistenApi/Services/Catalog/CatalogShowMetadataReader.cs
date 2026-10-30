using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Logging;

namespace Relisten.Catalog;

/// <summary>
/// Fills show metadata in batches for both navigation summaries and full recording reads.
/// The caller owns the snapshot. This reader never loads sources or tracks itself.
/// </summary>
public sealed class CatalogShowMetadataReader(ILogger<CatalogShowMetadataReader> logger)
{
    public async Task Apply(
        CatalogReadSession session,
        IReadOnlyList<CatalogShow> shows,
        CancellationToken cancellationToken
    )
    {
        if (shows.Count == 0)
        {
            return;
        }

        var venueIds = shows
            .Select(x => x.venue_id)
            .Concat(shows.SelectMany(x => x.Sources).Select(x => x.venue_id))
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .Distinct()
            .ToArray();
        var venues = (
            await session.Connection.QueryAsync<CatalogImportedVenue>(
                session.Command(
                    "SELECT v.*,a.uuid artist_uuid FROM venues v JOIN artists a ON a.id=v.artist_id WHERE v.id=ANY(@venueIds)",
                    new { venueIds },
                    cancellationToken
                )
            )
        ).ToDictionary(x => x.id);
        var yearUuids = shows
            .Where(x => x.year_uuid.HasValue)
            .Select(x => x.year_uuid!.Value)
            .Distinct()
            .ToArray();
        var years = (
            await session.Connection.QueryAsync<CatalogYear>(
                session.Command(
                    "SELECT y.*,a.uuid artist_uuid FROM years y JOIN artists a ON a.id=y.artist_id WHERE y.uuid=ANY(@yearUuids)",
                    new { yearUuids },
                    cancellationToken
                )
            )
        ).ToDictionary(x => x.uuid);
        var tourUuids = shows
            .Where(x => x.tour_uuid.HasValue)
            .Select(x => x.tour_uuid!.Value)
            .Distinct()
            .ToArray();
        var tours = (
            await session.Connection.QueryAsync<CatalogTour>(
                session.Command(
                    """
                    SELECT t.uuid,a.uuid artist_uuid,t.start_date::timestamp start_date,t.end_date::timestamp end_date,
                           t.name,t.slug,t.upstream_identifier,t.created_at,t.updated_at
                    FROM tours t JOIN artists a ON a.id=t.artist_id WHERE t.uuid=ANY(@tourUuids)
                    """,
                    new { tourUuids },
                    cancellationToken
                )
            )
        ).ToDictionary(x => x.uuid);
        var showUuids = shows.Select(x => x.uuid).ToArray();
        var eras = (
            await session.Connection.QueryAsync<ShowEra>(
                session.Command(
                    "SELECT s.uuid show_uuid,e.\"order\",e.name FROM eras e JOIN shows s ON s.era_id=e.id WHERE s.uuid=ANY(@showUuids)",
                    new { showUuids },
                    cancellationToken
                )
            )
        ).ToDictionary(x => x.show_uuid);

        foreach (var show in shows)
        {
            show.ImportedVenue = show.venue_id is { } venueId
                ? venues.GetValueOrDefault(venueId)
                : null;
            show.Year = show.year_uuid is { } yearId ? years.GetValueOrDefault(yearId) : null;
            show.Tour = show.tour_uuid is { } tourId ? tours.GetValueOrDefault(tourId) : null;
            show.Era = eras.TryGetValue(show.uuid, out var era)
                ? new() { order = era.order, name = era.name }
                : null;
            foreach (var source in show.Sources)
            {
                source.ImportedVenue = source.venue_id is { } sourceVenueId
                    ? venues.GetValueOrDefault(sourceVenueId)
                    : null;
            }
        }

        await ApplyVenues(session, shows, cancellationToken);
    }

    private async Task ApplyVenues(
        CatalogReadSession session,
        IReadOnlyList<CatalogShow> shows,
        CancellationToken cancellationToken
    )
    {
        var approvedShows = shows.Where(x => x.normalization_enabled).ToDictionary(x => x.uuid);
        if (approvedShows.Count == 0)
        {
            return;
        }

        // Carry show identity through the merge chain: one imported venue can map
        // differently on different dates. Cycles and dangling chains preserve fallback.
        var links = (
            await session.Connection.QueryAsync<ShowVenue, CatalogVenue, ShowVenue>(
                session.Command(
                    """
                    WITH RECURSIVE chain AS (
                      SELECT sv.show_uuid,v.*,ARRAY[v.id] path
                      FROM norm.show_venues sv JOIN norm.venues v ON v.id=sv.venue_id
                      WHERE sv.show_uuid=ANY(@showUuids)
                      UNION ALL
                      SELECT c.show_uuid,v.*,c.path || v.id
                      FROM chain c JOIN norm.venues v ON v.id=c.merged_into
                      WHERE cardinality(c.path)<32 AND NOT v.id=ANY(c.path)
                    )
                    SELECT c.show_uuid,c.id,c.name,c.kind,c.parent_id,c.city,c.region,c.country,
                           c.latitude,c.longitude,c.coordinate_precision,c.merged_into
                    FROM chain c
                    """,
                    new { showUuids = approvedShows.Keys.ToArray() },
                    cancellationToken
                ),
                (assignment, venue) =>
                {
                    assignment.Venue = venue;
                    return assignment;
                },
                splitOn: "id"
            )
        ).ToArray();
        foreach (var group in links.GroupBy(x => x.show_uuid))
        {
            var survivor = group.SingleOrDefault(x => x.Venue.merged_into is null);
            if (survivor is null)
            {
                logger.LogWarning(
                    "Invalid normalized venue merge chain for show {ShowUuid}",
                    group.Key
                );
                continue;
            }
            var show = approvedShows[group.Key];
            show.Venue = survivor.Venue;
        }

        await ReadVenueNames(
            session,
            approvedShows
                .Values.Where(show => show.Venue is not null)
                .Select(show => show.Venue!)
                .ToArray(),
            cancellationToken
        );
    }

    private static async Task ReadVenueNames(
        CatalogReadSession session,
        IReadOnlyList<CatalogVenue> venues,
        CancellationToken cancellationToken
    )
    {
        if (venues.Count == 0)
        {
            return;
        }

        // Names are aliases for discovery and details. Imported venue observations
        // supply show labels; repeated stored periods must not duplicate aliases.
        var ids = venues.Select(venue => venue.id).Distinct().ToArray();
        var names = await session.Connection.QueryAsync<CatalogVenueName>(
            session.Command(
                """
                WITH RECURSIVE lineage AS (
                  SELECT v.id canonical_venue_id,v.id venue_id,0 depth,ARRAY[v.id] path
                  FROM norm.venues v WHERE v.id=ANY(@ids)
                  UNION ALL
                  SELECT f.canonical_venue_id,v.id,f.depth+1,f.path || v.id
                  FROM lineage f JOIN norm.venues v ON v.merged_into=f.venue_id
                  WHERE f.depth<20 AND NOT v.id=ANY(f.path)
                )
                SELECT DISTINCT f.canonical_venue_id,n.name,n.kind
                FROM lineage f JOIN norm.venue_names n ON n.venue_id=f.venue_id
                ORDER BY f.canonical_venue_id,n.kind,n.name
                """,
                new { ids },
                cancellationToken
            )
        );
        var namesByVenue = names
            .GroupBy(name => name.canonical_venue_id)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<CatalogVenueName>)group.ToArray()
            );
        foreach (var venue in venues)
        {
            venue.Names = namesByVenue.GetValueOrDefault(venue.id) ?? [];
        }
    }

    internal const string ShowSelect = """
        SELECT s.id,s.uuid,s.venue_id,s.date::timestamp date,s.avg_rating,s.avg_duration,
               s.display_date,s.created_at,s.updated_at,a.uuid artist_uuid,y.uuid year_uuid,t.uuid tour_uuid,
               COALESCE(f.norm_catalogue,false) normalization_enabled,
               COALESCE(cnt.source_count,0)::int source_count,
               COALESCE(cnt.has_soundboard_source,false) has_soundboard_source,
               COALESCE(cnt.has_flac,false) has_streamable_flac_source,
               COALESCE(cnt.max_updated_at,s.updated_at) most_recent_source_updated_at
        FROM shows s JOIN artists a ON a.id=s.artist_id
        LEFT JOIN features f ON f.artist_id=a.id
        LEFT JOIN years y ON y.id=s.year_id LEFT JOIN tours t ON t.id=s.tour_id
        LEFT JOIN show_source_information cnt ON cnt.show_id=s.id
        """;

    private sealed class ShowEra
    {
        public Guid show_uuid { get; set; }
        public int order { get; set; }
        public string name { get; set; } = null!;
    }

    private sealed class ShowVenue
    {
        public Guid show_uuid { get; set; }
        public CatalogVenue Venue { get; set; } = null!;
    }
}
