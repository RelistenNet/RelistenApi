using System.Linq;
using HotChocolate;
using HotChocolate.Types;
using Relisten.Catalog;
using Relisten.Identifiers;

namespace Relisten.GraphQL;

internal static class CatalogGraphLookups
{
    public static void Configure(IObjectTypeDescriptor d)
    {
        Slug<ArtistType>(d.Field("artistBySlug"), CatalogReferenceKind.Artist, false);
        Slug<SongType>(d.Field("songBySlug"), CatalogReferenceKind.Song, true);
        Slug<VenueType>(d.Field("venueBySlug"), CatalogReferenceKind.Venue, false);
        Slug<TourType>(d.Field("tourBySlug"), CatalogReferenceKind.Tour, true);
        Slug<ImportedSongType>(
            d.Field("importedSongBySlug"),
            CatalogReferenceKind.ImportedSong,
            true
        );
        Slug<ImportedVenueType>(
            d.Field("importedVenueBySlug"),
            CatalogReferenceKind.ImportedVenue,
            true
        );
        Slug<SourceTrackType>(d.Field("sourceTrackBySlug"), CatalogReferenceKind.Track, true);
        foreach (var slug in new[] { false, true })
        {
            var field = d.Field(slug ? "artistVenueBySlug" : "artistVenueFor")
                .UseCatalogReader()
                .Type<ArtistVenueType>()
                .Argument("artistId", a => a.Type<NonNullType<IdType>>());
            field
                .Argument(slug ? "slug" : "venueId", a => a.Type<NonNullType<StringType>>())
                .Resolve(async c =>
                {
                    var artist = UuidTypeId.Parse(c.ArgumentValue<string>("artistId"), "artist");
                    var reader = c.Service<CatalogVenueReader>();
                    var detail = slug
                        ? await reader.ReadArtistVenueBySlug(
                            artist,
                            c.ArgumentValue<string>("slug"),
                            c.RequestAborted,
                            includeShows: false
                        )
                        : await ReadPair();
                    return detail is null
                        ? null
                        : Relisten.Api.Models.Api.V4.V4CatalogProjection.ArtistVenue(
                            detail.ArtistVenue
                        );

                    async System.Threading.Tasks.Task<CatalogArtistVenueDetail?> ReadPair()
                    {
                        var venue = c.ArgumentValue<string>("venueId");
                        Relisten.Normalization.VenueId.Parse(venue);
                        return await reader.ReadArtistVenue(
                            artist,
                            venue,
                            c.RequestAborted,
                            includeShows: false
                        );
                    }
                });
        }
        d.Field("yearByNumber")
            .UseCatalogReader()
            .Type<YearType>()
            .Argument("artistId", a => a.Type<NonNullType<IdType>>())
            .Argument("year", a => a.Type<NonNullType<IntType>>())
            .Resolve(async c =>
            {
                var year = c.ArgumentValue<int>("year");
                if (year is < 1 or > 9999)
                {
                    throw new System.FormatException("Invalid year.");
                }
                var detail = await c.Service<CatalogBrowseReader>()
                    .ReadYear(
                        UuidTypeId.Parse(c.ArgumentValue<string>("artistId"), "artist"),
                        null,
                        year,
                        c.RequestAborted,
                        includeShows: false
                    );
                return detail is null
                    ? null
                    : Relisten.Api.Models.Api.V4.V4CatalogProjection.Year(detail.Year);
            });
    }

    private static void Slug<T>(
        IObjectFieldDescriptor field,
        CatalogReferenceKind kind,
        bool artist
    )
        where T : ObjectType, new()
    {
        field.Type<T>().Argument("slug", a => a.Type<NonNullType<StringType>>());
        if (artist)
        {
            field.Argument("artistId", a => a.Type<NonNullType<IdType>>());
        }
        field.Resolve(async c =>
        {
            var reference = new CatalogReference(
                kind,
                CatalogReferenceScheme.LegacySlug,
                c.ArgumentValue<string>("slug"),
                artist ? UuidTypeId.Parse(c.ArgumentValue<string>("artistId"), "artist") : null
            );
            var result = await c.Service<CatalogGraphOperation>()
                .Resolve([reference], false, c.RequestAborted);
            if (result[0].Reason == "normalization_unavailable")
            {
                throw new CatalogNormalizationUnavailableException(
                    "The canonical identity cannot be resolved."
                );
            }
            return result[0].Target;
        });
    }
}
