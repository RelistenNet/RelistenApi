using System;
using System.Linq;
using HotChocolate;
using HotChocolate.Types;
using Relisten.Api.Models.Api.V4;
using Relisten.Catalog;
using Relisten.Data;
using Relisten.Identifiers;
using Relisten.Services.Live;

namespace Relisten.GraphQL;

internal static class CatalogGraphDiscovery
{
    public static void Configure(IObjectTypeDescriptor d)
    {
        CatalogGraphFields
            .Connection<ShowType>(
                d.Field("showsOnDate"),
                CatalogReferenceKind.Show,
                filters: (c, f) =>
                {
                    var month = c.ArgumentValue<int>("month");
                    var day = c.ArgumentValue<int>("day");
                    if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(2000, month))
                    {
                        throw new FormatException("Invalid anniversary date.");
                    }
                    return f with { Month = month, Day = day };
                }
            )
            .Argument("month", a => a.Type<NonNullType<IntType>>())
            .Argument("day", a => a.Type<NonNullType<IntType>>());
        CatalogGraphFields.Connection<ShowType>(
            d.Field("showsToday"),
            CatalogReferenceKind.Show,
            filters: (c, f) => f with { Month = 0, Day = 0 }
        );
        CatalogGraphFields
            .Connection<ShowType>(
                d.Field("topShows"),
                CatalogReferenceKind.Show,
                filters: (c, f) => f with { RequireSources = true }
            )
            .Argument(
                "orderBy",
                a => a.Type<EnumType<CatalogSort>>().DefaultValue(CatalogSort.Rating)
            );
        CatalogGraphFields
            .Connection<ShowType>(
                d.Field("recentlyPerformedShows"),
                CatalogReferenceKind.Show,
                filters: (c, f) =>
                    f with
                    {
                        Recency = CatalogRecency.Performed,
                        RecentDays = Days(c),
                    }
            )
            .Argument("days", a => a.Type<IntType>())
            .Argument(
                "orderBy",
                a => a.Type<EnumType<CatalogSort>>().DefaultValue(CatalogSort.DateDescending)
            );
        foreach (var added in new[] { false, true })
        {
            CatalogGraphFields
                .Connection<ShowType>(
                    d.Field(added ? "recentlyAddedShows" : "recentlyUpdatedShows"),
                    CatalogReferenceKind.Show,
                    filters: (c, f) =>
                        f with
                        {
                            Recency = added ? CatalogRecency.Added : CatalogRecency.Updated,
                            RecentDays = Days(c),
                        }
                )
                .Argument("days", a => a.Type<IntType>())
                .Argument(
                    "orderBy",
                    a =>
                        a.Type<EnumType<CatalogSort>>()
                            .DefaultValue(added ? CatalogSort.Added : CatalogSort.Updated)
                );
        }
        d.Field("randomShow")
            .UseCatalogReader()
            .Type<ShowType>()
            .Argument("artistId", a => a.Type<NonNullType<IdType>>())
            .Resolve(async c =>
            {
                var show = await c.Service<CatalogReader>()
                    .ReadRandomShow(
                        UuidTypeId.Parse(c.ArgumentValue<string>("artistId"), "artist"),
                        c.RequestAborted,
                        includeRecordings: false
                    );
                return show is null ? null : V4CatalogProjection.ShowSummary(show);
            });
        d.Field("showByDate")
            .UseCatalogReader()
            .Type<ShowType>()
            .Argument("artistId", a => a.Type<NonNullType<IdType>>())
            .Argument("date", a => a.Type<NonNullType<StringType>>())
            .Resolve(async c =>
            {
                var show = await c.Service<CatalogReader>()
                    .ReadShowByDate(
                        UuidTypeId.Parse(c.ArgumentValue<string>("artistId"), "artist"),
                        c.ArgumentValue<string>("date"),
                        c.RequestAborted,
                        includeRecordings: false
                    );
                return show is null ? null : V4CatalogProjection.ShowSummary(show);
            });
        d.Field("liveHistory")
            .UseCatalogReader(c => c.ArgumentValue<int>("limit") * 2)
            .Type<ListType<ObjectType<LiveHistoryResource>>>()
            .Argument("afterSequence", a => a.Type<IntType>())
            .Argument("limit", a => a.Type<IntType>().DefaultValue(20))
            .Argument("artistIds", a => a.Type<ListType<NonNullType<IdType>>>())
            .Cost(20)
            .ListSize(2000)
            .Resolve(async c =>
            {
                var limit = c.ArgumentValue<int>("limit");
                if (limit is < 1 or > 2000)
                {
                    throw new FormatException("Invalid history limit.");
                }
                var ids = c.ArgumentValue<string[]?>("artistIds") ?? [];
                if (ids.Length > c.Service<CatalogGraphOptions>().MaxIds)
                {
                    throw new FormatException("Too many artists.");
                }
                var artists = ids.Select(id => UuidTypeId.Parse(id, "artist")).ToArray();
                return (
                    await c.Service<CatalogLiveReader>()
                        .ReadHistory(
                            c.ArgumentValue<int?>("afterSequence"),
                            limit,
                            artists,
                            c.RequestAborted
                        )
                )
                    .Select(item => new LiveHistoryResource(
                        item.Play.id,
                        item.Play.created_at,
                        (PlayAppType)item.Play.app_type,
                        V4CatalogProjection.Track(item.Track),
                        V4CatalogProjection.ShowSummary(item.Show)
                    ))
                    .ToArray();
            });
        d.Field("liveSummary")
            .UseCatalogReader(c =>
                c.ArgumentValue<int>("showsLimit") + c.ArgumentValue<int>("artistsLimit")
            )
            .Type<ObjectType<LiveSummaryResource>>()
            .Argument("minutes", a => a.Type<IntType>().DefaultValue(15))
            .Argument("showsLimit", a => a.Type<IntType>().DefaultValue(20))
            .Argument("artistsLimit", a => a.Type<IntType>().DefaultValue(10))
            .Argument("monthDay", a => a.Type<StringType>())
            .Argument("sort", a => a.Type<StringType>().DefaultValue("surprise"))
            .Argument("maxPerArtist", a => a.Type<IntType>().DefaultValue(2))
            .Argument("freshDays", a => a.Type<IntType>().DefaultValue(7))
            .Cost(100)
            .Resolve(async c =>
            {
                var now = DateTime.UtcNow;
                if (
                    !LiveSummaryRequest.TryCreate(
                        c.ArgumentValue<int>("minutes"),
                        c.ArgumentValue<int>("showsLimit"),
                        c.ArgumentValue<int>("artistsLimit"),
                        c.ArgumentValue<string?>("monthDay"),
                        c.ArgumentValue<string>("sort"),
                        c.ArgumentValue<int>("maxPerArtist"),
                        c.ArgumentValue<int>("freshDays"),
                        now,
                        out var request,
                        out var error
                    )
                )
                {
                    throw new FormatException(error);
                }
                return await c.Service<CatalogLiveSummaryReader>()
                    .Read(request, LiveWindow.EndingBefore(now, request.Minutes), c.RequestAborted);
            });
    }

    private static int? Days(HotChocolate.Resolvers.IResolverContext c)
    {
        var days = c.ArgumentValue<int?>("days");
        if (days is < 1)
        {
            throw new FormatException("days must be positive.");
        }
        return days;
    }
}
