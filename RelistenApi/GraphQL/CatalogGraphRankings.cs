using System.Linq;
using HotChocolate;
using HotChocolate.Types;
using Relisten.Api.Models.Api.V4;
using Relisten.Catalog;
using Relisten.Services.Popularity;

namespace Relisten.GraphQL;

public sealed record CatalogGraphRank<T>(int Rank, T Node, PopularityResource Metrics);

public sealed class ShowRankType : ObjectType<CatalogGraphRank<ShowSummaryResource>>
{
    protected override void Configure(
        IObjectTypeDescriptor<CatalogGraphRank<ShowSummaryResource>> d
    )
    {
        d.Name("ShowRank");
        d.Field(r => r.Node).Type<ShowType>();
    }
}

public sealed class ArtistRankType : ObjectType<CatalogGraphRank<ArtistResource>>
{
    protected override void Configure(IObjectTypeDescriptor<CatalogGraphRank<ArtistResource>> d)
    {
        d.Name("ArtistRank");
        d.Field(r => r.Node).Type<ArtistType>();
    }
}

public sealed class YearRankType : ObjectType<CatalogGraphRank<YearResource>>
{
    protected override void Configure(IObjectTypeDescriptor<CatalogGraphRank<YearResource>> d)
    {
        d.Name("YearRank");
        d.Field(r => r.Node).Type<YearType>();
    }
}

internal static class CatalogGraphRankings
{
    public static void Configure(IObjectTypeDescriptor d)
    {
        Arguments(d.Field("artistRankings").Type<ListType<ArtistRankingGroupType>>(), grouped: true)
            .Argument("artistIds", a => a.Type<NonNullType<ListType<NonNullType<IdType>>>>())
            .Resolve(async c =>
            {
                var ids = c.ArgumentValue<string[]>("artistIds");
                if (ids.Length > c.Service<CatalogGraphOptions>().MaxIds)
                {
                    throw new GraphQLException("Too many artists.");
                }
                return await c.Service<CatalogRankingReader>()
                    .ReadArtistGroups(
                        ids.Select(id => Relisten.Identifiers.UuidTypeId.Parse(id, "artist"))
                            .Distinct()
                            .ToArray(),
                        Limit(c),
                        c.ArgumentValue<PopularitySortWindow>("window"),
                        c.RequestAborted
                    );
            });
        foreach (var trending in new[] { false, true })
        {
            var prefix = trending ? "trending" : "popular";
            Arguments(d.Field(prefix + "Shows").Type<ListType<ShowRankType>>())
                .Resolve(async c =>
                    (
                        await c.Service<CatalogRankingReader>()
                            .ReadShows(
                                trending,
                                Limit(c),
                                c.ArgumentValue<PopularitySortWindow>("window"),
                                c.RequestAborted
                            )
                    )
                        .Select(
                            (r, i) =>
                                new CatalogGraphRank<ShowSummaryResource>(
                                    i + 1,
                                    V4CatalogProjection.ShowSummary(r.Resource),
                                    V4CatalogProjection.ProjectPopularity(r.Metrics)
                                )
                        )
                        .ToArray()
                );
            Arguments(d.Field(prefix + "Artists").Type<ListType<ArtistRankType>>())
                .Resolve(async c =>
                    (
                        await c.Service<CatalogRankingReader>()
                            .ReadArtists(trending, Limit(c), c.RequestAborted)
                    )
                        .Select(
                            (r, i) =>
                                new CatalogGraphRank<ArtistResource>(
                                    i + 1,
                                    CatalogBrowseProjection.Artist(r.Resource),
                                    V4CatalogProjection.ProjectPopularity(r.Metrics)
                                )
                        )
                        .ToArray()
                );
            Arguments(d.Field(prefix + "Years").Type<ListType<YearRankType>>())
                .Resolve(async c =>
                    (
                        await c.Service<CatalogRankingReader>()
                            .ReadYears(trending, Limit(c), c.RequestAborted)
                    )
                        .Select(
                            (r, i) =>
                                new CatalogGraphRank<YearResource>(
                                    i + 1,
                                    V4CatalogProjection.Year(r.Resource),
                                    V4CatalogProjection.ProjectPopularity(r.Metrics)
                                )
                        )
                        .ToArray()
                );
        }
        Arguments(d.Field("momentumShows").Type<ListType<ShowRankType>>())
            .Argument("artistIds", a => a.Type<NonNullType<ListType<NonNullType<IdType>>>>())
            .Resolve(async c =>
                (
                    await c.Service<CatalogRankingReader>()
                        .ReadMomentum(
                            ArtistIds(c)
                                .Select(id => Relisten.Identifiers.UuidTypeId.Parse(id, "artist"))
                                .ToArray(),
                            Limit(c),
                            c.RequestAborted
                        )
                )
                    .Select(
                        (r, i) =>
                            new CatalogGraphRank<ShowSummaryResource>(
                                i + 1,
                                V4CatalogProjection.ShowSummary(r.Resource),
                                V4CatalogProjection.ProjectPopularity(r.Metrics)
                            )
                    )
                    .ToArray()
            );
    }

    private static string[] ArtistIds(HotChocolate.Resolvers.IResolverContext c)
    {
        var ids = c.ArgumentValue<string[]>("artistIds");
        if (ids.Length > c.Service<CatalogGraphOptions>().MaxIds)
        {
            throw new System.FormatException("Too many artists.");
        }
        return ids;
    }

    private static IObjectFieldDescriptor Arguments(
        IObjectFieldDescriptor d,
        bool grouped = false
    ) =>
        d.UseCatalogReader(c =>
                grouped ? checked(ArtistIds(c).Length * (1 + 2 * Limit(c))) : Limit(c)
            )
            .Cost(20)
            .Argument("limit", a => a.Type<IntType>().DefaultValue(50))
            .Argument(
                "window",
                a =>
                    a.Type<EnumType<PopularitySortWindow>>()
                        .DefaultValue(PopularitySortWindow.Days30)
            )
            .ListSize(
                assumedSize: 500,
                slicingArguments: grouped ? null : ["limit"],
                requireOneSlicingArgument: false,
                slicingArgumentDefaultValue: grouped ? null : 50
            );

    private static int Limit(HotChocolate.Resolvers.IResolverContext c)
    {
        var value = c.ArgumentValue<int>("limit");
        if (value is < 1 or > 500)
        {
            throw new GraphQLException("limit must be between 1 and 500.");
        }
        return value;
    }
}

public sealed class ArtistRankingGroupType : ObjectType<CatalogArtistRankings>
{
    protected override void Configure(IObjectTypeDescriptor<CatalogArtistRankings> d)
    {
        d.Name("ArtistRankingGroup");
        d.BindFieldsExplicitly();
        d.Field("artistId")
            .Type<IdType>()
            .Resolve(c =>
                Relisten.Identifiers.UuidTypeId.Format(
                    "artist",
                    c.Parent<CatalogArtistRankings>().ArtistUuid
                )
            );
        CatalogGraphFields.Entity<ArtistType>(
            d.Field("artist"),
            CatalogReferenceKind.Artist,
            c =>
                Relisten.Identifiers.UuidTypeId.Format(
                    "artist",
                    c.Parent<CatalogArtistRankings>().ArtistUuid
                )
        );
        d.Field("popularShows")
            .Type<ListType<ShowRankType>>()
            .ListSize(50)
            .Resolve(c =>
                c.Parent<CatalogArtistRankings>()
                    .PopularShows.Select(
                        (show, i) =>
                            new CatalogGraphRank<ShowSummaryResource>(
                                i + 1,
                                V4CatalogProjection.ShowSummary(show),
                                show.Popularity is { } metrics
                                    ? V4CatalogProjection.ProjectPopularity(metrics)
                                    : null!
                            )
                    )
                    .ToArray()
            );
        d.Field("trendingShows")
            .Type<ListType<ShowRankType>>()
            .ListSize(50)
            .Resolve(c =>
                c.Parent<CatalogArtistRankings>()
                    .TrendingShows.Select(
                        (show, i) =>
                            new CatalogGraphRank<ShowSummaryResource>(
                                i + 1,
                                V4CatalogProjection.ShowSummary(show),
                                show.Popularity is { } metrics
                                    ? V4CatalogProjection.ProjectPopularity(metrics)
                                    : null!
                            )
                    )
                    .ToArray()
            );
    }
}
