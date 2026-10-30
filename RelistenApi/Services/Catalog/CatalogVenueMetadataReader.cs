using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Logging;

namespace Relisten.Catalog;

/// <summary>
/// Resolves normalized venue identities and their aliases in the caller's snapshot.
/// Artist approval and show membership belong to the caller, not global venue metadata.
/// </summary>
public sealed class CatalogVenueMetadataReader(ILogger<CatalogVenueMetadataReader> logger)
{
    /// <summary>
    /// Maps requested IDs to surviving venues. Unknown IDs and broken merge chains are
    /// omitted, allowing show reads to retain imported fallback and direct reads to return 404.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, CatalogVenue>> Read(
        CatalogReadSession session,
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken
    )
    {
        var requestedIds = ids.Distinct().ToArray();
        if (requestedIds.Length == 0)
        {
            return new Dictionary<string, CatalogVenue>();
        }

        var paths = await ReadMergePaths(session, requestedIds, cancellationToken);
        var resolved = ResolveVenues(requestedIds, paths);
        await ReadNames(
            session,
            resolved.Values.DistinctBy(venue => venue.id).ToArray(),
            cancellationToken
        );
        return resolved;
    }

    private static async Task<IReadOnlyList<VenuePath>> ReadMergePaths(
        CatalogReadSession session,
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken
    ) =>
        (
            await session.Connection.QueryAsync<string, CatalogVenue, VenuePath>(
                session.Command(
                    """
                    WITH RECURSIVE chain AS (
                      SELECT v.id requested_id,v.*,ARRAY[v.id] path
                      FROM norm.venues v WHERE v.id=ANY(@ids)
                      UNION ALL
                      SELECT c.requested_id,v.*,c.path || v.id
                      FROM chain c JOIN norm.venues v ON v.id=c.merged_into
                      WHERE cardinality(c.path)<32 AND NOT v.id=ANY(c.path)
                    )
                    SELECT c.requested_id,c.id,c.name,c.kind,c.parent_id,c.city,c.region,c.country,
                           c.latitude,c.longitude,c.coordinate_precision,c.merged_into
                    FROM chain c
                    """,
                    new { ids },
                    cancellationToken
                ),
                (requestedId, venue) => new(requestedId, venue),
                splitOn: "id"
            )
        ).ToArray();

    private Dictionary<string, CatalogVenue> ResolveVenues(
        IReadOnlyList<string> ids,
        IReadOnlyList<VenuePath> paths
    )
    {
        var pathsById = paths.ToLookup(path => path.RequestedId);
        var survivors = new Dictionary<string, CatalogVenue>();
        var resolved = new Dictionary<string, CatalogVenue>();
        foreach (var id in ids)
        {
            var path = pathsById[id].ToArray();
            if (path.Length == 0)
            {
                logger.LogDebug("Unknown normalized venue {VenueId}", id);
                continue;
            }

            var survivor = path.Select(item => item.Venue)
                .SingleOrDefault(venue => venue.merged_into is null);
            if (survivor is null)
            {
                logger.LogWarning("Invalid normalized venue merge chain for {VenueId}", id);
                continue;
            }

            if (!survivors.TryGetValue(survivor.id, out var canonical))
            {
                survivors.Add(survivor.id, survivor);
                canonical = survivor;
            }
            resolved.Add(id, canonical);
        }
        return resolved;
    }

    private static async Task ReadNames(
        CatalogReadSession session,
        IReadOnlyList<CatalogVenue> venues,
        CancellationToken cancellationToken
    )
    {
        if (venues.Count == 0)
        {
            return;
        }

        // Merges retain aliases on their original rows. Load the whole surviving lineage
        // once per batch; repeated periods do not create duplicate public name/kind pairs.
        var ids = venues.Select(venue => venue.id).ToArray();
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

    private sealed record VenuePath(string RequestedId, CatalogVenue Venue);
}
