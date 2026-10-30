using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;

namespace Relisten.Catalog;

/// <summary>Reads imported tour navigation and complete catalog membership in one snapshot.</summary>
public sealed class CatalogTourReader(
    DbService db,
    CatalogTourMetadataReader tourMetadata,
    CatalogShowMetadataReader showMetadata
)
{
    /// <summary>Known artists with no tours return an empty collection.</summary>
    public async Task<IReadOnlyList<CatalogTour>?> ReadTours(
        Guid artistUuid,
        CancellationToken cancellationToken = default
    )
    {
        await using var session = await CatalogReadSession.Open(db, cancellationToken);
        return await ReadTours(session, artistUuid, cancellationToken);
    }

    internal async Task<IReadOnlyList<CatalogTour>?> ReadTours(
        CatalogReadSession session,
        Guid artistUuid,
        CancellationToken cancellationToken
    )
    {
        var artistExists = await session.Connection.QuerySingleAsync<bool>(
            session.Command(
                "SELECT EXISTS(SELECT 1 FROM artists WHERE uuid=@artistUuid)",
                new { artistUuid },
                cancellationToken
            )
        );
        if (!artistExists)
        {
            return null;
        }

        var tours = await tourMetadata.ReadForArtist(session, artistUuid, cancellationToken);
        return tours
            .Values.OrderBy(tour => tour.start_date ?? DateTime.MaxValue)
            .ThenBy(tour => tour.uuid)
            .ToArray();
    }

    /// <summary>A global typed tour ID returns its metadata and complete show summaries.</summary>
    public async Task<CatalogTourDetail?> ReadTour(
        Guid tourUuid,
        CancellationToken cancellationToken = default
    )
    {
        await using var session = await CatalogReadSession.Open(db, cancellationToken);
        return await ReadTour(session, tourUuid, cancellationToken);
    }

    internal async Task<CatalogTourDetail?> ReadTour(
        CatalogReadSession session,
        Guid tourUuid,
        CancellationToken cancellationToken
    )
    {
        var tours = await tourMetadata.Read(session, [tourUuid], cancellationToken);
        if (!tours.TryGetValue(tourUuid, out var tour))
        {
            return null;
        }

        var shows = (
            await session.Connection.QueryAsync<CatalogShow>(
                session.Command(
                    CatalogShowMetadataReader.ShowSelect
                        + " WHERE s.artist_id=@artistId AND s.tour_id=@tourId ORDER BY s.display_date,s.uuid",
                    new { artistId = tour.artist_id, tourId = tour.id },
                    cancellationToken
                )
            )
        ).ToArray();
        await showMetadata.Apply(session, shows, cancellationToken);
        return new(tour, shows);
    }
}
