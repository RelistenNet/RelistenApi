using System;
using System.Threading.Tasks;
using HotChocolate;
using HotChocolate.Resolvers;
using HotChocolate.Types;
using Relisten.Catalog;
using Relisten.Identifiers;
using Relisten.Normalization;

namespace Relisten.GraphQL;

public sealed class CatalogGraphFilter
{
    public string? ArtistId { get; set; }
    public DateTime? From { get; set; }
    public DateTime? Through { get; set; }
    public string? VenueId { get; set; }
    public string? SongId { get; set; }
    public bool? Soundboard { get; set; }
    public bool? Flac { get; set; }
    public string? Search { get; set; }
    public bool IncludeAutoCreated { get; set; }

    internal CatalogFilters Read()
    {
        if (VenueId is { } venue)
        {
            Relisten.Normalization.VenueId.Parse(venue);
        }
        if (SongId is { } song)
        {
            Relisten.Normalization.SongId.Parse(song);
        }
        if (From > Through)
        {
            throw new FormatException("The date range is reversed.");
        }
        return new(
            ArtistId is { } artist ? UuidTypeId.Parse(artist, "artist") : null,
            From,
            Through,
            VenueId,
            SongId,
            Soundboard,
            Flac,
            Search,
            IncludeAutoCreated
        );
    }
}

internal static class CatalogGraphFields
{
    internal static IObjectFieldDescriptor UseCatalogReader(
        this IObjectFieldDescriptor field,
        Func<IResolverContext, int>? rows = null
    ) =>
        field.Use(next =>
            async context =>
            {
                var operation = context.Service<CatalogGraphOperation>();
                operation.ChargeRows(rows?.Invoke(context) ?? 1);
                await operation.ExecuteReader(
                    async () =>
                    {
                        await next(context);
                        return true;
                    },
                    context.RequestAborted
                );
            }
        );

    public static IObjectFieldDescriptor Entity<T>(
        IObjectFieldDescriptor field,
        CatalogReferenceKind kind,
        Func<IResolverContext, string?>? id = null
    )
        where T : ObjectType, new()
    {
        field.Type<T>();
        if (id is null)
        {
            field.Argument("id", a => a.Type<NonNullType<IdType>>());
        }
        return field.Resolve(async context =>
        {
            var value = id is null ? context.ArgumentValue<string>("id") : id(context);
            if (value is null)
            {
                return null;
            }
            Validate(kind, value);
            var recording =
                kind == CatalogReferenceKind.Source
                && (context.IsSelected("sets") || context.IsSelected("links"));
            var result = await context
                .DataLoader<CatalogEntityLoader>()
                .LoadAsync(new CatalogEntityLoad(kind, value, recording), context.RequestAborted);
            if (result?.Reason == "normalization_unavailable")
            {
                throw new CatalogNormalizationUnavailableException(
                    "The canonical identity cannot be resolved."
                );
            }
            if (result?.Availability == CatalogReferenceAvailability.Retired)
            {
                throw new CatalogRetiredResourceException();
            }
            return result?.Resource;
        });
    }

    public static IObjectFieldDescriptor Connection<T>(
        IObjectFieldDescriptor field,
        CatalogReferenceKind kind,
        Func<IResolverContext, (CatalogReferenceKind Kind, string Id)>? owner = null,
        Func<IResolverContext, CatalogFilters, CatalogFilters>? filters = null,
        bool songShows = false,
        bool includeFallback = false
    )
        where T : ObjectType, new()
    {
        return field
            .Type<CatalogConnectionType<T>>()
            .Cost(10)
            .ListSize(
                assumedSize: 500,
                slicingArguments: ["first"],
                sizedFields: ["nodes", "edges"],
                requireOneSlicingArgument: false,
                slicingArgumentDefaultValue: 50
            )
            .Argument("first", a => a.Type<IntType>().DefaultValue(50))
            .Argument("after", a => a.Type<StringType>())
            .Argument("filter", a => a.Type<InputObjectType<CatalogGraphFilter>>())
            .Argument(
                "orderBy",
                a => a.Type<EnumType<CatalogSort>>().DefaultValue(CatalogSort.Default)
            )
            .Resolve(async context =>
            {
                var first = context.ArgumentValue<int>("first");
                if (first < 1 || first > context.Service<CatalogGraphOptions>().MaxPageSize)
                {
                    throw new GraphQLException("first must be between 1 and 500.");
                }
                var parent = owner?.Invoke(context);
                var selectedFilters =
                    context.ArgumentValue<CatalogGraphFilter?>("filter")?.Read() ?? new();
                selectedFilters = filters?.Invoke(context, selectedFilters) ?? selectedFilters;
                var collection = new CatalogCollection(
                    kind,
                    parent?.Kind,
                    parent?.Id,
                    selectedFilters,
                    context.ArgumentValue<CatalogSort>("orderBy"),
                    includeFallback
                );
                ValidateCollection(collection);
                if (selectedFilters.Search is { Length: < 3 })
                {
                    return new CatalogGraphPage(
                        [],
                        [],
                        new(null, false),
                        context.IsSelected("totalCount") ? 0 : null
                    );
                }

                var after = CatalogCursor.Decode(
                    collection,
                    context.ArgumentValue<string?>("after")
                );
                var page = new CatalogPageRequest(first, after, context.IsSelected("totalCount"));
                var recordings =
                    kind == CatalogReferenceKind.Source
                    && (SelectsNodeField(context, "sets") || SelectsNodeField(context, "links"));
                var statistics = songShows && SelectsNodeField(context, "timings");
                return await context
                    .DataLoader<CatalogPageLoader>()
                    .LoadAsync(
                        new CatalogPageLoad(collection, page, recordings, songShows, statistics),
                        context.RequestAborted
                    );
            });
    }

    // IsSelected arguments are sibling alternatives, not a path. Use the single-field
    // collection overload: HC 16.7.3's multi-field overload does not unwrap list types.
    private static bool SelectsNodeField(IResolverContext context, string field) =>
        context.Select("nodes").IsSelected(field)
        || context.Select("edges").Select("node").IsSelected(field);

    private static void ValidateCollection(CatalogCollection collection)
    {
        var f = collection.Filters ?? new();
        var recording =
            collection.Kind
            is CatalogReferenceKind.Show
                or CatalogReferenceKind.Source
                or CatalogReferenceKind.Track;
        if (
            !recording
            && (
                f.From is not null
                || f.Through is not null
                || f.Venue is not null
                || f.Song is not null
                || f.Soundboard is not null
                || f.Flac is not null
            )
        )
        {
            throw new FormatException(
                "Recording filters require a show, source, or track collection."
            );
        }
        if (f.IncludeAutoCreated && collection.Kind != CatalogReferenceKind.Artist)
        {
            throw new FormatException("includeAutoCreated applies only to artist browsing.");
        }
        if (
            f.Search is not null
            && collection.Kind is CatalogReferenceKind.Year or CatalogReferenceKind.SourceReview
        )
        {
            throw new FormatException("Search is not supported on this collection.");
        }
        var supported =
            collection.Sort == CatalogSort.Default
            || collection.Kind switch
            {
                CatalogReferenceKind.Show => true,
                CatalogReferenceKind.Artist => collection.Sort == CatalogSort.Popularity,
                CatalogReferenceKind.Year => collection.Sort == CatalogSort.Rating
                    || collection.Sort == CatalogSort.Popularity
                        && (
                            f.Artist is not null
                            || collection.OwnerKind == CatalogReferenceKind.Artist
                        ),
                CatalogReferenceKind.Tour => collection.Sort == CatalogSort.DateDescending,
                CatalogReferenceKind.Source => collection.Sort
                    is CatalogSort.Rating
                        or CatalogSort.DateDescending
                        or CatalogSort.Updated
                        or CatalogSort.Added,
                _ => false,
            };
        if (!supported)
        {
            throw new FormatException(
                "The requested ordering is not supported on this collection."
            );
        }
    }

    internal static void Validate(CatalogReferenceKind kind, string id)
    {
        switch (kind)
        {
            case CatalogReferenceKind.Song:
                SongId.Parse(id);
                break;
            case CatalogReferenceKind.Venue:
                VenueId.Parse(id);
                break;
            case CatalogReferenceKind.ArtistVenue:
                ArtistVenueId.Parse(id);
                break;
            default:
                UuidTypeId.Parse(id, CatalogReferenceLookup.Prefix(kind));
                break;
        }
    }
}
