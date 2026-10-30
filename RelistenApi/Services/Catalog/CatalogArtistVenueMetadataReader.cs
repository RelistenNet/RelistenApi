using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;

namespace Relisten.Catalog;

/// <summary>
/// Batch-reads persisted artist/place relationships in the caller's snapshot. Counts
/// are never recomputed by reads. The caller attaches its resolved canonical venue.
/// </summary>
public sealed class CatalogArtistVenueMetadataReader
{
    public async Task<IReadOnlyDictionary<(Guid Artist, string Venue), CatalogArtistVenue>> Read(
        CatalogReadSession session,
        IReadOnlyList<Guid> artistUuids,
        IReadOnlyList<string>? venueIds,
        CancellationToken cancellationToken
    )
    {
        if (artistUuids.Count == 0 || venueIds is { Count: 0 })
        {
            return new Dictionary<(Guid, string), CatalogArtistVenue>();
        }

        var rows = await session.Connection.QueryAsync<CatalogArtistVenue>(
            session.Command(
                """
                SELECT av.id,av.artist_uuid,av.venue_id,av.artist_show_count,av.created_at,av.updated_at
                FROM norm.artist_venues av JOIN artists a ON a.uuid=av.artist_uuid
                JOIN features f ON f.artist_id=a.id AND f.norm_catalogue
                WHERE av.artist_uuid=ANY(@artists)
                  AND (@venues::text[] IS NULL OR av.venue_id=ANY(@venues))
                """,
                new
                {
                    artists = artistUuids.Distinct().ToArray(),
                    venues = venueIds?.Distinct().ToArray(),
                },
                cancellationToken
            )
        );
        return rows.ToDictionary(row => (row.artist_uuid, row.venue_id));
    }
}
