using System;
using HotChocolate;
using HotChocolate.Types;
using Relisten.Catalog;
using Relisten.Identifiers;

namespace Relisten.GraphQL;

public sealed record CatalogGraphSearch(string Query, Guid? Artist);

public sealed class CatalogSearchType : ObjectType<CatalogGraphSearch>
{
    protected override void Configure(IObjectTypeDescriptor<CatalogGraphSearch> d)
    {
        d.Name("CatalogSearch");
        d.BindFieldsExplicitly();
        CatalogGraphFields.Connection<ArtistType>(
            d.Field("artists"),
            CatalogReferenceKind.Artist,
            filters: Search
        );
        CatalogGraphFields.Connection<ShowType>(
            d.Field("shows"),
            CatalogReferenceKind.Show,
            filters: Search
        );
        CatalogGraphFields.Connection<SourceType>(
            d.Field("sources"),
            CatalogReferenceKind.Source,
            filters: Search
        );
        CatalogGraphFields.Connection<SongEntryType>(
            d.Field("songs"),
            CatalogReferenceKind.Song,
            filters: Search,
            includeFallback: true
        );
        CatalogGraphFields.Connection<TourType>(
            d.Field("tours"),
            CatalogReferenceKind.Tour,
            filters: Search
        );
        CatalogGraphFields.Connection<VenueEntryType>(
            d.Field("venues"),
            CatalogReferenceKind.ArtistVenue,
            filters: Search,
            includeFallback: true
        );
    }

    private static CatalogFilters Search(
        HotChocolate.Resolvers.IResolverContext c,
        CatalogFilters filters
    )
    {
        var search = c.Parent<CatalogGraphSearch>();
        if (filters.Artist is { } artist && search.Artist is { } root && artist != root)
        {
            throw new FormatException("The category artist conflicts with the search artist.");
        }
        if (filters.Search is { } query && query.Trim() != search.Query)
        {
            throw new FormatException("The category query conflicts with the search query.");
        }
        return filters with { Search = search.Query, Artist = search.Artist ?? filters.Artist };
    }
}
