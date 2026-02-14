using System;
using System.Threading.Tasks;
using Hangfire;

namespace Relisten.Services.Popularity
{
    public class PopularityJobs
    {
        private readonly PopularityService popularityService;

        public PopularityJobs(PopularityService popularityService)
        {
            this.popularityService = popularityService;
        }

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
        public Task RefreshPopularShows(int limit, PopularitySortWindow sortWindow = PopularitySortWindow.Days30)
        {
            return popularityService.RefreshPopularShows(limit, sortWindow);
        }

        [Queue("default")]
        public Task RefreshTrendingShows(int limit, PopularitySortWindow sortWindow = PopularitySortWindow.Days30)
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
