using System.Linq;
using Relisten.Api.Models;
using Relisten.Catalog;

namespace Relisten.Api.Models.Api.V4;

public static partial class V4CatalogProjection
{
    public static RankedArtistResource RankedArtist(CatalogRanked<CatalogArtist> value, int rank) =>
        CatalogBrowseProjection.ProjectArtist<RankedArtistResource>(value.Resource) with
        {
            Rank = rank,
            Popularity = Metrics(value.Metrics),
        };

    public static RankedYearResource RankedYear(CatalogRanked<CatalogYear> value, int rank) =>
        ProjectYear<RankedYearResource>(value.Resource) with
        {
            Rank = rank,
            Popularity = Metrics(value.Metrics),
        };

    public static RankedShowResource RankedShow(CatalogRanked<CatalogShow> value, int rank) =>
        ProjectShow<RankedShowResource>(value.Resource) with
        {
            Rank = rank,
            Popularity = Metrics(value.Metrics),
        };

    public static ArtistRankingResource RankingGroup(CatalogArtistRankings value) =>
        new(
            new(value.ArtistUuid),
            value.PopularShows.Select((item, index) => RankedShow(item, index + 1)).ToArray(),
            value.TrendingShows.Select((item, index) => RankedShow(item, index + 1)).ToArray()
        );

    private static RankingMetricsResource Metrics(PopularityMetrics metrics)
    {
        static RankingWindowResource Window(PopularityWindowMetrics value) =>
            new(
                value.plays,
                System.Math.Round(value.hours, 4),
                System.Math.Round(value.hot_score, 4)
            );
        return new(
            System.Math.Round(metrics.momentum_score, 4),
            System.Math.Round(metrics.trend_ratio, 4),
            new(
                Window(metrics.windows.hours_48h),
                Window(metrics.windows.days_7d),
                Window(metrics.windows.days_30d)
            )
        );
    }
}
