using System;
using System.Collections.Generic;
using Relisten.Api.Models;

namespace Relisten.Api.Models.Api
{
    public class PopularArtistListItem
    {
        public int rank { get; set; }
        public Guid artist_uuid { get; set; }
        public string name { get; set; } = string.Empty;
        public long plays_30d { get; set; }
        public long plays_48h { get; set; }
        public double trend_ratio { get; set; }
        public PopularityMetrics popularity { get; set; } = new();
    }

    public class ArtistPopularTrendingShowsResponse
    {
        public Guid artist_uuid { get; set; }
        public string artist_name { get; set; } = string.Empty;
        public IReadOnlyList<Show> popular_shows { get; set; } = new List<Show>();
        public IReadOnlyList<Show> trending_shows { get; set; } = new List<Show>();
    }

    public class MultiArtistPopularTrendingShowsResponse
    {
        public IReadOnlyList<ArtistPopularTrendingShowsResponse> artists { get; set; } =
            new List<ArtistPopularTrendingShowsResponse>();
    }

    public class PopularYearListItem
    {
        public int rank { get; set; }
        public Guid year_uuid { get; set; }
        public string year { get; set; } = string.Empty;
        public Guid artist_uuid { get; set; }
        public string artist_name { get; set; } = string.Empty;
        public long plays_30d { get; set; }
        public long plays_48h { get; set; }
        public double trend_ratio { get; set; }
        public PopularityMetrics popularity { get; set; } = new();
    }
}
