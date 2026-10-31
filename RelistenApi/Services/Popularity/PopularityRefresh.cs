using System.Threading;
using System.Threading.Tasks;

namespace Relisten.Services.Popularity;

public partial class PopularityService
{
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
