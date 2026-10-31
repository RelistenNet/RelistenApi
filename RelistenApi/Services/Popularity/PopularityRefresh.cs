using System.Threading;
using System.Threading.Tasks;

namespace Relisten.Services.Popularity;

public partial class PopularityService
{
    /// <summary>
    /// Processes a read's stale set using the existing dual-publishing calculations.
    /// Retries recalculate even if Postgres is fresh: an earlier attempt may have
    /// committed there before Redis publication failed.
    /// </summary>
    public async Task RefreshPopulations(
        PopularityPopulation[] populations,
        CancellationToken cancellationToken = default
    )
    {
        foreach (var population in populations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (population.Kind)
            {
                case PopularityPopulationKind.Artist:
                    await RefreshArtistPopularityMap();
                    break;
                case PopularityPopulationKind.Show when population.Artist is { } artist:
                    await RefreshShowPopularityMapForArtist(artist);
                    break;
                case PopularityPopulationKind.Show:
                    await RefreshGlobalShowPopularityMap();
                    break;
                case PopularityPopulationKind.Year when population.Artist is { } artist:
                    await RefreshYearPopularityMapForArtist(artist);
                    break;
                case PopularityPopulationKind.Year:
                    await RefreshGlobalYearPopularityMap();
                    break;
                default:
                    throw new System.ArgumentOutOfRangeException(nameof(populations));
            }
        }
    }

    /// <summary>
    /// Publishes the rolling populations to Postgres and the legacy Redis maps.
    /// Each population commits independently; a failed job retries without
    /// discarding successful populations or the last successful failed one.
    /// </summary>
    public async Task RefreshReadModel(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await RefreshArtistPopularityMap();
        await RefreshGlobalShowPopularityMap();
        await RefreshGlobalYearPopularityMap();

        foreach (var artist in await store.ReadArtistsToRefresh(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RefreshShowPopularityMapForArtist(artist);
            await RefreshYearPopularityMapForArtist(artist);
        }
    }
}
