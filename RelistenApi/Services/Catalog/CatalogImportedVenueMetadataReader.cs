using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dapper;

namespace Relisten.Catalog;

/// <summary>
/// Reads imported venues with their full show-level counts in the caller's snapshot.
/// Counts include normalized shows and exclude source-only venue observations.
/// </summary>
public sealed class CatalogImportedVenueMetadataReader
{
    public Task<IEnumerable<CatalogImportedVenue>> Read(
        CatalogReadSession session,
        long[] venueIds,
        CancellationToken cancellationToken
    ) =>
        session.Connection.QueryAsync<CatalogImportedVenue>(
            session.Command(
                Select + " WHERE v.id=ANY(@venueIds)",
                new { venueIds },
                cancellationToken
            )
        );

    // The existing (artist_id, venue_id) index supports these counts without scanning
    // the catalog. Use the same definition in embedded resources and venue collections.
    internal const string Select = """
        SELECT v.*,a.uuid artist_uuid,counts.show_count
        FROM venues v JOIN artists a ON a.id=v.artist_id
        CROSS JOIN LATERAL (
            SELECT count(*)::int show_count FROM shows s
            WHERE s.artist_id=v.artist_id AND s.venue_id=v.id
        ) counts
        """;
}
