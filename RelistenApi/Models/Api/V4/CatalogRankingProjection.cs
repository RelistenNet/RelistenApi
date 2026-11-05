using System.Linq;
using Relisten.Catalog;

namespace Relisten.Api.Models.Api.V4;

public static partial class V4CatalogProjection
{
    public static RankedArtistResource RankedArtist(CatalogRanked<CatalogArtist> value, int rank) =>
        CatalogBrowseProjection.ProjectArtist<RankedArtistResource>(value.Resource) with
        {
            Rank = rank,
            Popularity = ProjectPopularity(value.Metrics),
        };

    public static RankedYearResource RankedYear(CatalogRanked<CatalogYear> value, int rank) =>
        ProjectYear<RankedYearResource>(value.Resource) with
        {
            Rank = rank,
            Popularity = ProjectPopularity(value.Metrics),
        };

    public static RankedShowResource RankedShow(CatalogShow value, int rank) =>
        ProjectShow<RankedShowResource>(value) with
        {
            Rank = rank,
        };

    public static ArtistRankingResource RankingGroup(CatalogArtistRankings value) =>
        new(
            new(value.ArtistUuid),
            value.PopularShows.Select((item, index) => RankedShow(item, index + 1)).ToArray(),
            value.TrendingShows.Select((item, index) => RankedShow(item, index + 1)).ToArray()
        );
}
