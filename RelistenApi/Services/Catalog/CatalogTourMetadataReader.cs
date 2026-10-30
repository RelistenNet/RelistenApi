using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;

namespace Relisten.Catalog;

/// <summary>Shares tour metadata and catalog show counts across list, detail, and embedded reads.</summary>
public sealed class CatalogTourMetadataReader
{
    public Task<IReadOnlyDictionary<Guid, CatalogTour>> ReadForArtist(
        CatalogReadSession session,
        Guid artistUuid,
        CancellationToken cancellationToken
    ) => Read(session, "a.uuid=@artistUuid", new { artistUuid }, cancellationToken);

    public Task<IReadOnlyDictionary<Guid, CatalogTour>> Read(
        CatalogReadSession session,
        IReadOnlyList<Guid> tourUuids,
        CancellationToken cancellationToken
    ) => Read(session, "t.uuid=ANY(@tourUuids)", new { tourUuids }, cancellationToken);

    private static async Task<IReadOnlyDictionary<Guid, CatalogTour>> Read(
        CatalogReadSession session,
        string predicate,
        object parameters,
        CancellationToken cancellationToken
    )
    {
        var tours = (
            await session.Connection.QueryAsync<CatalogTour>(
                session.Command(
                    """
                    SELECT t.id,t.artist_id,t.uuid,a.uuid artist_uuid,
                           t.start_date::timestamp start_date,t.end_date::timestamp end_date,
                           t.name,t.slug,t.upstream_identifier,t.created_at,t.updated_at
                    FROM tours t JOIN artists a ON a.id=t.artist_id
                    WHERE
                    """
                        + " "
                        + predicate,
                    parameters,
                    cancellationToken
                )
            )
        ).ToArray();
        if (tours.Length == 0)
        {
            return new Dictionary<Guid, CatalogTour>();
        }

        // Count only the selected artists and tours. Setlist-only appearances do not
        // belong to the playable catalog collection returned by tour detail.
        var counts = (
            await session.Connection.QueryAsync<TourCount>(
                session.Command(
                    """
                    SELECT s.tour_id,count(*)::int show_count
                    FROM shows s JOIN tours t ON t.id=s.tour_id AND t.artist_id=s.artist_id
                    WHERE s.artist_id=ANY(@artistIds) AND s.tour_id=ANY(@tourIds)
                    GROUP BY s.tour_id
                    """,
                    new
                    {
                        artistIds = tours.Select(tour => tour.artist_id).Distinct().ToArray(),
                        tourIds = tours.Select(tour => tour.id).ToArray(),
                    },
                    cancellationToken
                )
            )
        ).ToDictionary(row => row.tour_id, row => row.show_count);
        foreach (var tour in tours)
        {
            tour.show_count = counts.GetValueOrDefault(tour.id);
        }
        return tours.ToDictionary(tour => tour.uuid);
    }

    private sealed class TourCount
    {
        public long tour_id { get; set; }
        public int show_count { get; set; }
    }
}
