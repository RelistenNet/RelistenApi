using System;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;

namespace Relisten.Services.Popularity
{
    [SuccessExpiration(5)]
    public class PopularityJobs
    {
        private readonly PopularityService popularityService;

        public PopularityJobs(PopularityService popularityService)
        {
            this.popularityService = popularityService;
        }

        /// <summary>Explicit full backfill/repair; this is not a recurring job.</summary>
        [Queue("default")]
        public Task RefreshReadModel(CancellationToken cancellationToken) =>
            popularityService.RefreshReadModel(cancellationToken);

        /// <summary>Recalculates the stale populations selected by one catalog request.</summary>
        [Queue("default")]
        public Task RefreshPopulations(
            PopularityPopulation[] populations,
            CancellationToken cancellationToken
        ) => popularityService.RefreshPopulations(populations, cancellationToken);

        [Queue("default")]
        public Task RefreshGlobalShowPopularityMap() =>
            popularityService.RefreshGlobalShowPopularityMap();

        [Queue("default")]
        public Task RefreshGlobalYearPopularityMap() =>
            popularityService.RefreshGlobalYearPopularityMap();

        [Queue("default")]
        public Task RefreshArtistPopularityMap()
        {
            return popularityService.RefreshArtistPopularityMap();
        }

        [Queue("default")]
        public Task RefreshShowPopularityMapForArtist(Guid artistUuid)
        {
            return popularityService.RefreshShowPopularityMapForArtist(artistUuid);
        }

        [Queue("default")]
        public Task RefreshYearPopularityMapForArtist(Guid artistUuid)
        {
            return popularityService.RefreshYearPopularityMapForArtist(artistUuid);
        }

        [Queue("default")]
        public Task RefreshPopularArtists(int limit)
        {
            return popularityService.RefreshPopularArtists(limit);
        }

        [Queue("default")]
        public Task RefreshTrendingArtists(int limit)
        {
            return popularityService.RefreshTrendingArtists(limit);
        }

        [Queue("default")]
        public Task RefreshPopularShows(
            int limit,
            PopularitySortWindow sortWindow = PopularitySortWindow.Days30
        )
        {
            return popularityService.RefreshPopularShows(limit, sortWindow);
        }

        [Queue("default")]
        public Task RefreshTrendingShows(
            int limit,
            PopularitySortWindow sortWindow = PopularitySortWindow.Days30
        )
        {
            return popularityService.RefreshTrendingShows(limit, sortWindow);
        }

        [Queue("default")]
        public Task RefreshPopularYears(int limit)
        {
            return popularityService.RefreshPopularYears(limit);
        }

        [Queue("default")]
        public Task RefreshTrendingYears(int limit)
        {
            return popularityService.RefreshTrendingYears(limit);
        }
    }
}
