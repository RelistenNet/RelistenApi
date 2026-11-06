using System.Linq;
using System.Threading.Tasks;
using HotChocolate;
using HotChocolate.Types;
using Relisten.Api.Models.Api.V4;
using Relisten.Catalog;

namespace Relisten.GraphQL;

public sealed class ArtistType : ObjectType<ArtistResource>
{
    protected override void Configure(IObjectTypeDescriptor<ArtistResource> d)
    {
        d.Name("Artist");
        d.BindFieldsImplicitly();
        CatalogGraphFields.Connection<ShowType>(
            d.Field("shows"),
            CatalogReferenceKind.Show,
            c => (CatalogReferenceKind.Artist, c.Parent<ArtistResource>().Id.ToString())
        );
        CatalogGraphFields.Connection<YearType>(
            d.Field("years"),
            CatalogReferenceKind.Year,
            c => (CatalogReferenceKind.Artist, c.Parent<ArtistResource>().Id.ToString())
        );
        CatalogGraphFields.Connection<TourType>(
            d.Field("tours"),
            CatalogReferenceKind.Tour,
            c => (CatalogReferenceKind.Artist, c.Parent<ArtistResource>().Id.ToString())
        );
        CatalogGraphFields.Connection<SongEntryType>(
            d.Field("songs"),
            CatalogReferenceKind.Song,
            c => (CatalogReferenceKind.Artist, c.Parent<ArtistResource>().Id.ToString()),
            includeFallback: true
        );
        CatalogGraphFields.Connection<ImportedSongType>(
            d.Field("importedSongs"),
            CatalogReferenceKind.ImportedSong,
            c => (CatalogReferenceKind.Artist, c.Parent<ArtistResource>().Id.ToString())
        );
        CatalogGraphFields.Connection<VenueEntryType>(
            d.Field("venues"),
            CatalogReferenceKind.ArtistVenue,
            c => (CatalogReferenceKind.Artist, c.Parent<ArtistResource>().Id.ToString()),
            includeFallback: true
        );
        CatalogGraphFields.Connection<ImportedVenueType>(
            d.Field("importedVenues"),
            CatalogReferenceKind.ImportedVenue,
            c => (CatalogReferenceKind.Artist, c.Parent<ArtistResource>().Id.ToString())
        );
        d.Field("eras")
            .UseCatalogReader()
            .Type<ListType<ObjectType<EraResource>>>()
            .Resolve(async c =>
                (
                    await c.Service<CatalogBrowseReader>()
                        .ReadEras(c.Parent<ArtistResource>().Id.Value, c.RequestAborted)
                )
                    ?.Select(V4CatalogProjection.Era)
                    .ToArray()
            );
    }
}

public sealed class ShowType : ObjectType<ShowSummaryResource>
{
    protected override void Configure(IObjectTypeDescriptor<ShowSummaryResource> d)
    {
        d.Name("Show");
        d.IsOfType((_, value) => value is ShowSummaryResource);
        d.BindFieldsImplicitly();
        CatalogGraphFields.Connection<SourceType>(
            d.Field("sources"),
            CatalogReferenceKind.Source,
            c => (CatalogReferenceKind.Show, c.Parent<ShowSummaryResource>().Id.ToString())
        );
        CatalogGraphFields.Entity<ArtistType>(
            d.Field("artist"),
            CatalogReferenceKind.Artist,
            c => c.Parent<ShowSummaryResource>().ArtistId is { } id ? id.ToString() : null
        );
        CatalogGraphFields.Entity<YearType>(
            d.Field("year"),
            CatalogReferenceKind.Year,
            c => c.Parent<ShowSummaryResource>().YearId is { } id ? id.ToString() : null
        );
        CatalogGraphFields.Entity<TourType>(
            d.Field("tour"),
            CatalogReferenceKind.Tour,
            c => c.Parent<ShowSummaryResource>().TourId is { } id ? id.ToString() : null
        );
    }
}

public sealed class SourceType : ObjectType<SourceSummaryResource>
{
    protected override void Configure(IObjectTypeDescriptor<SourceSummaryResource> d)
    {
        d.Name("Source");
        d.IsOfType((_, value) => value is SourceSummaryResource);
        d.BindFieldsImplicitly();
        CatalogGraphFields.Connection<SourceReviewType>(
            d.Field("reviews"),
            CatalogReferenceKind.SourceReview,
            c => (CatalogReferenceKind.Source, c.Parent<SourceSummaryResource>().Id.ToString())
        );
        CatalogGraphFields.Entity<ArtistType>(
            d.Field("artist"),
            CatalogReferenceKind.Artist,
            c => c.Parent<SourceSummaryResource>().ArtistId is { } id ? id.ToString() : null
        );
        CatalogGraphFields.Entity<ShowType>(
            d.Field("show"),
            CatalogReferenceKind.Show,
            c => c.Parent<SourceSummaryResource>().ShowId is { } id ? id.ToString() : null
        );
        d.Field("sets")
            .ListSize(assumedSize: 4)
            .Type<ListType<NonNullType<SourceSetType>>>()
            .Resolve(c =>
            {
                var value = c.Parent<SourceSummaryResource>();
                return (value as SourceResource)?.Sets;
            });
        d.Field("links")
            .Type<ListType<NonNullType<ObjectType<LinkResource>>>>()
            .Resolve(c =>
            {
                var value = c.Parent<SourceSummaryResource>();
                return (value as SourceResource)?.Links;
            });
    }
}

public sealed class SourceSetType : ObjectType<SourceSetSummaryResource>
{
    protected override void Configure(IObjectTypeDescriptor<SourceSetSummaryResource> d)
    {
        d.Name("SourceSet");
        d.IsOfType((_, value) => value is SourceSetSummaryResource);
        d.BindFieldsImplicitly();
        CatalogGraphFields.Entity<ArtistType>(
            d.Field("artist"),
            CatalogReferenceKind.Artist,
            c => c.Parent<SourceSetSummaryResource>().ArtistId is { } id ? id.ToString() : null
        );
        CatalogGraphFields.Entity<SourceType>(
            d.Field("source"),
            CatalogReferenceKind.Source,
            c => c.Parent<SourceSetSummaryResource>().SourceId is { } id ? id.ToString() : null
        );
        d.Field("tracks")
            .ListSize(assumedSize: 50)
            .Type<ListType<NonNullType<SourceTrackType>>>()
            .Resolve(async c =>
            {
                var set = c.Parent<SourceSetSummaryResource>();
                if (set is SourceSetResource loaded)
                {
                    return loaded.Tracks;
                }
                var result = await c.DataLoader<CatalogEntityLoader>()
                    .LoadAsync(
                        new CatalogEntityLoad(
                            CatalogReferenceKind.Source,
                            set.SourceId.ToString(),
                            true
                        ),
                        c.RequestAborted
                    );
                return (result?.Resource as SourceResource)
                    ?.Sets.FirstOrDefault(s => s.Id == set.Id)
                    ?.Tracks;
            });
    }
}

public sealed class SourceTrackType : ObjectType<TrackResource>
{
    protected override void Configure(IObjectTypeDescriptor<TrackResource> d)
    {
        d.Name("SourceTrack");
        d.BindFieldsImplicitly();
        CatalogGraphFields.Entity<ArtistType>(
            d.Field("artist"),
            CatalogReferenceKind.Artist,
            c => c.Parent<TrackResource>().ArtistId is { } id ? id.ToString() : null
        );
        CatalogGraphFields.Entity<ShowType>(
            d.Field("show"),
            CatalogReferenceKind.Show,
            c => c.Parent<TrackResource>().ShowId is { } id ? id.ToString() : null
        );
        CatalogGraphFields.Entity<SourceType>(
            d.Field("source"),
            CatalogReferenceKind.Source,
            c => c.Parent<TrackResource>().SourceId is { } id ? id.ToString() : null
        );
        CatalogGraphFields.Entity<SourceSetType>(
            d.Field("sourceSet"),
            CatalogReferenceKind.SourceSet,
            c => c.Parent<TrackResource>().SourceSetId is { } id ? id.ToString() : null
        );
    }
}

public sealed class YearType : ObjectType<YearResource>
{
    protected override void Configure(IObjectTypeDescriptor<YearResource> d)
    {
        d.Name("Year");
        d.BindFieldsImplicitly();
        CatalogGraphFields.Connection<ShowType>(
            d.Field("shows"),
            CatalogReferenceKind.Show,
            c => (CatalogReferenceKind.Year, c.Parent<YearResource>().Id.ToString())
        );
        CatalogGraphFields.Entity<ArtistType>(
            d.Field("artist"),
            CatalogReferenceKind.Artist,
            c => c.Parent<YearResource>().ArtistId is { } id ? id.ToString() : null
        );
    }
}

public sealed class TourType : ObjectType<TourResource>
{
    protected override void Configure(IObjectTypeDescriptor<TourResource> d)
    {
        d.Name("Tour");
        d.BindFieldsImplicitly();
        CatalogGraphFields.Connection<ShowType>(
            d.Field("shows"),
            CatalogReferenceKind.Show,
            c => (CatalogReferenceKind.Tour, c.Parent<TourResource>().Id.ToString())
        );
        CatalogGraphFields.Entity<ArtistType>(
            d.Field("artist"),
            CatalogReferenceKind.Artist,
            c => c.Parent<TourResource>().ArtistId is { } id ? id.ToString() : null
        );
    }
}

public sealed class SongType : ObjectType<SongDetailResource>
{
    protected override void Configure(IObjectTypeDescriptor<SongDetailResource> d)
    {
        d.Name("Song");
        d.BindFieldsImplicitly();
        CatalogGraphFields.Connection<SongShowType>(
            d.Field("shows"),
            CatalogReferenceKind.Show,
            c => (CatalogReferenceKind.Song, c.Parent<SongDetailResource>().Id.ToString()),
            songShows: true
        );
        CatalogGraphFields.Connection<SourceTrackType>(
            d.Field("tracks"),
            CatalogReferenceKind.Track,
            c => (CatalogReferenceKind.Song, c.Parent<SongDetailResource>().Id.ToString())
        );
        CatalogGraphFields.Entity<ArtistType>(
            d.Field("artist"),
            CatalogReferenceKind.Artist,
            c => c.Parent<SongDetailResource>().ArtistId is { } id ? id.ToString() : null
        );
    }
}

public sealed class ImportedSongType : ObjectType<ImportedSongResource>
{
    protected override void Configure(IObjectTypeDescriptor<ImportedSongResource> d)
    {
        d.Name("ImportedSong");
        d.BindFieldsImplicitly();
        CatalogGraphFields.Connection<ShowType>(
            d.Field("shows"),
            CatalogReferenceKind.Show,
            c => (CatalogReferenceKind.ImportedSong, c.Parent<ImportedSongResource>().Id.ToString())
        );
        CatalogGraphFields.Entity<ArtistType>(
            d.Field("artist"),
            CatalogReferenceKind.Artist,
            c => c.Parent<ImportedSongResource>().ArtistId is { } id ? id.ToString() : null
        );
        CatalogGraphFields.Entity<SongType>(
            d.Field("normalizedSong"),
            CatalogReferenceKind.Song,
            c => c.Parent<ImportedSongResource>().NormalizedSongId is { } id ? id.ToString() : null
        );
    }
}

public sealed class VenueType : ObjectType<VenueResource>
{
    protected override void Configure(IObjectTypeDescriptor<VenueResource> d)
    {
        d.Name("Venue");
        d.BindFieldsImplicitly();
        CatalogGraphFields.Connection<ShowType>(
            d.Field("shows"),
            CatalogReferenceKind.Show,
            c => (CatalogReferenceKind.Venue, c.Parent<VenueResource>().Id.ToString())
        );
        CatalogGraphFields.Entity<VenueType>(
            d.Field("parent"),
            CatalogReferenceKind.Venue,
            c => c.Parent<VenueResource>().ParentId is { } id ? id.ToString() : null
        );
    }
}

public sealed class ImportedVenueType : ObjectType<ImportedVenueResource>
{
    protected override void Configure(IObjectTypeDescriptor<ImportedVenueResource> d)
    {
        d.Name("ImportedVenue");
        d.BindFieldsImplicitly();
        CatalogGraphFields.Connection<ShowType>(
            d.Field("shows"),
            CatalogReferenceKind.Show,
            c =>
                (
                    CatalogReferenceKind.ImportedVenue,
                    c.Parent<ImportedVenueResource>().Id.ToString()
                )
        );
        CatalogGraphFields.Entity<ArtistType>(
            d.Field("artist"),
            CatalogReferenceKind.Artist,
            c => c.Parent<ImportedVenueResource>().ArtistId is { } id ? id.ToString() : null
        );
    }
}

public sealed class ArtistVenueType : ObjectType<ArtistVenueResource>
{
    protected override void Configure(IObjectTypeDescriptor<ArtistVenueResource> d)
    {
        d.Name("ArtistVenue");
        d.BindFieldsImplicitly();
        CatalogGraphFields.Connection<ShowType>(
            d.Field("shows"),
            CatalogReferenceKind.Show,
            c => (CatalogReferenceKind.ArtistVenue, c.Parent<ArtistVenueResource>().Id.ToString())
        );
        CatalogGraphFields.Entity<ArtistType>(
            d.Field("artist"),
            CatalogReferenceKind.Artist,
            c => c.Parent<ArtistVenueResource>().ArtistId is { } id ? id.ToString() : null
        );
    }
}

public sealed class SourceReviewType : ObjectType<SourceReviewResource>
{
    protected override void Configure(IObjectTypeDescriptor<SourceReviewResource> d)
    {
        d.Name("SourceReview");
        d.BindFieldsImplicitly();
        CatalogGraphFields.Entity<SourceType>(
            d.Field("source"),
            CatalogReferenceKind.Source,
            c => c.Parent<SourceReviewResource>().SourceId is { } id ? id.ToString() : null
        );
    }
}

public sealed class SetlistShowType : ObjectType<SetlistShowResource>
{
    protected override void Configure(IObjectTypeDescriptor<SetlistShowResource> d)
    {
        d.Name("SetlistShow");
        d.BindFieldsImplicitly();
        CatalogGraphFields.Entity<ArtistType>(
            d.Field("artist"),
            CatalogReferenceKind.Artist,
            c => c.Parent<SetlistShowResource>().ArtistId is { } id ? id.ToString() : null
        );
        CatalogGraphFields.Entity<ImportedVenueType>(
            d.Field("importedVenue"),
            CatalogReferenceKind.ImportedVenue,
            c => c.Parent<SetlistShowResource>().ImportedVenueId is { } id ? id.ToString() : null
        );
        CatalogGraphFields.Entity<TourType>(
            d.Field("tour"),
            CatalogReferenceKind.Tour,
            c => c.Parent<SetlistShowResource>().TourId is { } id ? id.ToString() : null
        );
    }
}

public sealed class TrackPartType : ObjectType<TrackPartResource>
{
    protected override void Configure(IObjectTypeDescriptor<TrackPartResource> d)
    {
        d.Name("TrackPart");
        // Identity, name and kind were interpreted with the recording. Keep that
        // materialized interpretation; full catalog traversal is a separate unit.
        d.Field(p => p.Song).Type<TrackPartSongType>();
    }
}

public sealed record CatalogGraphSongShow(
    ShowSummaryResource Show,
    SongShowTimingsResource Timings,
    string SongId
);

public sealed class SongShowType : ObjectType<CatalogGraphSongShow>
{
    protected override void Configure(IObjectTypeDescriptor<CatalogGraphSongShow> d)
    {
        d.Name("SongShow");
        d.BindFieldsExplicitly();
        d.Field(s => s.Show).Type<ShowType>();
        d.Field(s => s.Timings);
        CatalogGraphFields
            .Connection<SourceTrackType>(
                d.Field("tracks"),
                CatalogReferenceKind.Track,
                c => (CatalogReferenceKind.Song, c.Parent<CatalogGraphSongShow>().SongId),
                (c, f) =>
                    f with
                    {
                        ShowUuid = c.Parent<CatalogGraphSongShow>().Show.Id.Value,
                        TopSourceOnly =
                            c.ArgumentValue<CatalogSongShowTracks>("selection")
                            == CatalogSongShowTracks.Top,
                    }
            )
            .Argument(
                "selection",
                a => a.Type<SongTrackSelectionType>().DefaultValue(CatalogSongShowTracks.All)
            );
    }
}

public sealed class SongEntryType : ObjectType<ArtistSongEntryResource>
{
    protected override void Configure(IObjectTypeDescriptor<ArtistSongEntryResource> d)
    {
        d.Name("SongEntry");
        d.Field(e => e.Song).Type<SongType>();
        d.Field(e => e.ImportedSong).Type<ImportedSongType>();
    }
}

public sealed class VenueEntryType : ObjectType<ArtistVenueEntryResource>
{
    protected override void Configure(IObjectTypeDescriptor<ArtistVenueEntryResource> d)
    {
        d.Name("VenueEntry");
        d.Field(e => e.ArtistVenue).Type<ArtistVenueType>();
        d.Field(e => e.ImportedVenue).Type<ImportedVenueType>();
    }
}

public sealed class TrackNormalizationType : ObjectType<TrackNormalizationResource>
{
    protected override void Configure(IObjectTypeDescriptor<TrackNormalizationResource> d)
    {
        d.Name("TrackNormalization");
        d.Field(n => n.Parts).ListSize(assumedSize: 4);
    }
}

public sealed class LiveShowType : ObjectType<LiveShowResource>
{
    protected override void Configure(IObjectTypeDescriptor<LiveShowResource> d)
    {
        d.Name("LiveShow");
        CatalogGraphFields.Entity<SourceType>(
            d.Field(l => l.Source),
            CatalogReferenceKind.Source,
            c => c.Parent<LiveShowResource>().Source.Id.ToString()
        );
    }
}

public sealed class SongTrackSelectionType : EnumType<CatalogSongShowTracks>
{
    protected override void Configure(IEnumTypeDescriptor<CatalogSongShowTracks> d)
    {
        d.Name("SongTrackSelection");
        d.BindValuesExplicitly();
        d.Value(CatalogSongShowTracks.All);
        d.Value(CatalogSongShowTracks.Top);
    }
}

public sealed class TrackPartSongType : ObjectType<SongResource>
{
    protected override void Configure(IObjectTypeDescriptor<SongResource> d)
    {
        d.Name("TrackPartSong");
        CatalogGraphFields.Entity<SongType>(
            d.Field("catalog"),
            CatalogReferenceKind.Song,
            c => c.Parent<SongResource>().Id.ToString()
        );
        CatalogGraphFields.Entity<ArtistType>(
            d.Field("artist"),
            CatalogReferenceKind.Artist,
            c => c.Parent<SongResource>().ArtistId.ToString()
        );
    }
}
