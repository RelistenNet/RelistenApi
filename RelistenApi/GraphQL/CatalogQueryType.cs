using System.Linq;
using HotChocolate;
using HotChocolate.Types;
using Relisten.Catalog;

namespace Relisten.GraphQL;

public sealed class CatalogQueryType : ObjectType
{
    protected override void Configure(IObjectTypeDescriptor d)
    {
        d.Name("Query");
        CatalogGraphRankings.Configure(d);
        CatalogGraphDiscovery.Configure(d);
        CatalogGraphLookups.Configure(d);
        d.Field("search")
            .Type<CatalogSearchType>()
            .Argument("q", a => a.Type<NonNullType<StringType>>())
            .Argument("artistId", a => a.Type<IdType>())
            .Resolve(c => new CatalogGraphSearch(
                c.ArgumentValue<string>("q").Trim(),
                c.ArgumentValue<string?>("artistId") is { } artist
                    ? Relisten.Identifiers.UuidTypeId.Parse(artist, "artist")
                    : null
            ));
        d.Field("resolve")
            .Type<ListType<CatalogResolutionType>>()
            .Argument(
                "references",
                a =>
                    a.Type<
                        NonNullType<ListType<NonNullType<InputObjectType<CatalogGraphReference>>>>
                    >()
            )
            .Cost(50)
            .ListSize(500)
            .Resolve(async c =>
            {
                var inputs = c.ArgumentValue<CatalogGraphReference[]>("references");
                if (inputs.Length > c.Service<CatalogGraphOptions>().MaxIds)
                {
                    throw new GraphQLException("At most 500 references are accepted.");
                }
                return await c.Service<CatalogGraphOperation>()
                    .Resolve(
                        inputs.Select(i => i.Read()).ToArray(),
                        c.Select("target").IsSelected("sets")
                            || c.Select("target").IsSelected("links"),
                        c.RequestAborted
                    );
            });
        d.Field("sourceTracksByIds")
            .Type<ListType<SourceTrackType>>()
            .Argument("ids", a => a.Type<NonNullType<ListType<NonNullType<IdType>>>>())
            .Cost(10)
            .ListSize(500)
            .Resolve(async c =>
            {
                var ids = c.ArgumentValue<string[]>("ids");
                if (ids.Length > c.Service<CatalogGraphOptions>().MaxIds)
                {
                    throw new GraphQLException("At most 500 track IDs are accepted.");
                }
                foreach (var id in ids)
                {
                    CatalogGraphFields.Validate(CatalogReferenceKind.Track, id);
                }
                var result = await c.DataLoader<CatalogEntityLoader>()
                    .LoadAsync(
                        ids.Select(id => new CatalogEntityLoad(CatalogReferenceKind.Track, id))
                            .ToArray(),
                        c.RequestAborted
                    );
                return result.Select(r => r?.Resource).ToArray();
            });
        CatalogGraphFields.Entity<ArtistType>(d.Field("artist"), CatalogReferenceKind.Artist);
        CatalogGraphFields.Connection<ArtistType>(d.Field("artists"), CatalogReferenceKind.Artist);
        CatalogGraphFields.Entity<ShowType>(d.Field("show"), CatalogReferenceKind.Show);
        CatalogGraphFields.Connection<ShowType>(d.Field("shows"), CatalogReferenceKind.Show);
        CatalogGraphFields.Entity<SourceType>(d.Field("source"), CatalogReferenceKind.Source);
        CatalogGraphFields.Connection<SourceType>(d.Field("sources"), CatalogReferenceKind.Source);
        CatalogGraphFields.Entity<SourceSetType>(
            d.Field("sourceSet"),
            CatalogReferenceKind.SourceSet
        );
        CatalogGraphFields.Entity<SourceTrackType>(
            d.Field("sourceTrack"),
            CatalogReferenceKind.Track
        );
        CatalogGraphFields.Connection<SourceTrackType>(
            d.Field("sourceTracks"),
            CatalogReferenceKind.Track
        );
        CatalogGraphFields.Entity<YearType>(d.Field("year"), CatalogReferenceKind.Year);
        CatalogGraphFields.Connection<YearType>(d.Field("years"), CatalogReferenceKind.Year);
        CatalogGraphFields.Entity<TourType>(d.Field("tour"), CatalogReferenceKind.Tour);
        CatalogGraphFields.Connection<TourType>(d.Field("tours"), CatalogReferenceKind.Tour);
        CatalogGraphFields.Entity<SongType>(d.Field("song"), CatalogReferenceKind.Song);
        CatalogGraphFields.Connection<SongType>(d.Field("songs"), CatalogReferenceKind.Song);
        CatalogGraphFields.Entity<ImportedSongType>(
            d.Field("importedSong"),
            CatalogReferenceKind.ImportedSong
        );
        CatalogGraphFields.Connection<ImportedSongType>(
            d.Field("importedSongs"),
            CatalogReferenceKind.ImportedSong
        );
        CatalogGraphFields.Entity<VenueType>(d.Field("venue"), CatalogReferenceKind.Venue);
        CatalogGraphFields.Connection<VenueType>(d.Field("venues"), CatalogReferenceKind.Venue);
        CatalogGraphFields.Entity<ImportedVenueType>(
            d.Field("importedVenue"),
            CatalogReferenceKind.ImportedVenue
        );
        CatalogGraphFields.Connection<ImportedVenueType>(
            d.Field("importedVenues"),
            CatalogReferenceKind.ImportedVenue
        );
        CatalogGraphFields.Entity<ArtistVenueType>(
            d.Field("artistVenue"),
            CatalogReferenceKind.ArtistVenue
        );
        CatalogGraphFields.Connection<ArtistVenueType>(
            d.Field("artistVenues"),
            CatalogReferenceKind.ArtistVenue
        );
        CatalogGraphFields.Entity<SourceReviewType>(
            d.Field("sourceReview"),
            CatalogReferenceKind.SourceReview
        );
        CatalogGraphFields.Entity<SetlistShowType>(
            d.Field("setlistShow"),
            CatalogReferenceKind.SetlistShow
        );
    }
}
