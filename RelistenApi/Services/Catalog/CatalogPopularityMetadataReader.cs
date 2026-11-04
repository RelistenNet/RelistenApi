using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Relisten.Services.Popularity;

namespace Relisten.Catalog;

/// <summary>
/// Adds stored scores to ordinary artist/year resources in the caller's snapshot.
/// Artists use global scores; years use their artist's population, like ordinary shows.
/// Missing or inactive scores remain null. Refreshes use the existing stale-read queue.
/// </summary>
public sealed class CatalogPopularityMetadataReader(PopularityRefreshRequest refresh)
{
    public async Task ApplyArtists(
        CatalogReadSession session,
        IReadOnlyList<CatalogArtist> artists,
        CancellationToken cancellationToken
    )
    {
        if (artists.Count == 0)
        {
            return;
        }

        refresh.Require(PopularityPopulationKind.Artist);
        var rows = await session.Connection.QueryAsync<StoredPopularity>(
            session.Command(
                "SELECT * FROM artist_popularity WHERE uuid=ANY(@ids) AND momentum_score IS NOT NULL",
                new { ids = artists.Select(artist => artist.uuid).ToArray() },
                cancellationToken
            )
        );
        var metrics = rows.ToDictionary(row => row.uuid, row => row.ToMetrics());
        foreach (var artist in artists)
        {
            artist.Popularity = metrics.GetValueOrDefault(artist.uuid);
        }
    }

    public async Task ApplyYears(
        CatalogReadSession session,
        IReadOnlyList<CatalogYear> years,
        CancellationToken cancellationToken
    )
    {
        if (years.Count == 0)
        {
            return;
        }

        foreach (var artist in years.Select(year => year.artist_uuid).Distinct())
        {
            refresh.Require(PopularityPopulationKind.Year, artist);
        }
        var rows = await session.Connection.QueryAsync<StoredPopularity>(
            session.Command(
                """
                SELECT p.* FROM year_popularity p
                JOIN years y ON y.uuid=p.uuid JOIN artists a ON a.id=y.artist_id
                WHERE NOT p.is_global AND p.uuid=ANY(@ids)
                  AND p.artist_uuid=a.uuid AND p.momentum_score IS NOT NULL
                """,
                new { ids = years.Select(year => year.uuid).ToArray() },
                cancellationToken
            )
        );
        var metrics = rows.ToDictionary(row => row.uuid, row => row.ToMetrics());
        foreach (var year in years)
        {
            year.Popularity = metrics.GetValueOrDefault(year.uuid);
        }
    }
}
