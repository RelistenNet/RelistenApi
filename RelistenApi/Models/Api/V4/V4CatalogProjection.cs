using System;
using System.Linq;
using Relisten.Catalog;
using Relisten.Identifiers;
using Relisten.Normalization;

namespace Relisten.Api.Models.Api.V4;

/// <summary>Projects materialized catalog facts without further database reads.</summary>
public static class V4CatalogProjection
{
    public static ShowResource Show(CatalogShow value) =>
        ProjectShow<ShowResource>(value) with
        {
            Sources = value.Sources.Select(Source).ToArray(),
        };

    public static ShowSummaryResource ShowSummary(CatalogShow value) =>
        ProjectShow<ShowSummaryResource>(value);

    private static T ProjectShow<T>(CatalogShow value)
        where T : ShowSummaryResource, new() =>
        new()
        {
            Id = new(value.uuid),
            ArtistId = new(value.artist_uuid),
            YearId = value.year_uuid is { } idYearId ? new YearId(idYearId) : null,
            TourId = value.tour_uuid is { } idTourId ? new TourId(idTourId) : null,
            Date = value.date,
            DisplayDate = value.display_date,
            AvgRating = value.avg_rating,
            AvgDuration = value.avg_duration,
            MostRecentSourceUpdatedAt = value.most_recent_source_updated_at,
            HasSoundboardSource = value.has_soundboard_source,
            HasStreamableFlacSource = value.has_streamable_flac_source,
            SourceCount = value.source_count,
            CreatedAt = value.created_at,
            UpdatedAt = value.updated_at,
            ImportedVenue = value.ImportedVenue is { } imported ? ImportedVenue(imported) : null,
            Venue = value.Venue is { } venue ? Venue(venue) : null,
            Year = value.Year is { } year ? Year(year) : null,
            Tour = value.Tour is { } tour ? Tour(tour) : null,
            Era = value.Era is { } era ? Era(era) : null,
        };

    public static SourceResource Source(CatalogSource value) =>
        new()
        {
            Id = new(value.uuid),
            ArtistId = new(value.artist_uuid),
            ShowId = new(value.show_uuid),
            DisplayDate = value.display_date,
            IsSoundboard = value.is_soundboard,
            IsRemaster = value.is_remaster,
            HasJamcharts = value.has_jamcharts,
            AvgRating = value.avg_rating,
            NumReviews = value.num_reviews,
            NumRatings = value.num_ratings,
            AvgRatingWeighted = value.avg_rating_weighted,
            Duration = value.duration,
            UpstreamIdentifier = value.upstream_identifier,
            Description = value.description,
            TaperNotes = value.taper_notes,
            Source = value.source,
            Taper = value.taper,
            Transferrer = value.transferrer,
            Lineage = value.lineage,
            FlacType = value.flac_type switch
            {
                0 => SourceFlacType.NoFlac,
                1 => SourceFlacType.Flac16Bit,
                2 => SourceFlacType.Flac24Bit,
                3 => SourceFlacType.NoPlayableFlac,
                _ => SourceFlacType.Unknown,
            },
            ReviewCount = value.review_count,
            CreatedAt = value.created_at,
            UpdatedAt = value.updated_at,
            ImportedVenue = value.ImportedVenue is { } imported ? ImportedVenue(imported) : null,
            Sets = value.Sets.Select(Set).ToArray(),
            Links = value.Links.Select(Link).ToArray(),
        };

    public static SourceSetResource Set(CatalogSet value) =>
        new()
        {
            Id = new(value.uuid),
            SourceId = new(value.source_uuid),
            ArtistId = new(value.artist_uuid),
            Index = value.index,
            IsEncore = value.is_encore,
            Name = value.name,
            CreatedAt = value.created_at,
            UpdatedAt = value.updated_at,
            Tracks = value.Tracks.Select(Track).ToArray(),
        };

    public static TrackResource Track(CatalogTrack value) =>
        new()
        {
            Id = new(value.uuid),
            SourceId = new(value.source_uuid),
            SourceSetId = new(value.source_set_uuid),
            ShowId = new(value.show_uuid),
            ArtistId = new(value.artist_uuid),
            TrackPosition = value.track_position,
            Duration = value.duration,
            Title = value.title,
            Slug = value.slug,
            Mp3Url = value.mp3_url,
            Mp3Md5 = value.mp3_md5,
            FlacUrl = value.flac_url,
            FlacMd5 = value.flac_md5,
            CreatedAt = value.created_at,
            UpdatedAt = value.updated_at,
            Normalization = new(
                value.Normalization.State switch
                {
                    "unavailable" => TrackNormalizationState.Unavailable,
                    "partial" => TrackNormalizationState.Partial,
                    "interpreted" => TrackNormalizationState.Interpreted,
                    _ => throw new InvalidOperationException(
                        "Unsupported track normalization state."
                    ),
                },
                value.Normalization.Reason switch
                {
                    "disabled" => TrackNormalizationUnavailableReason.Disabled,
                    "not_interpreted" => TrackNormalizationUnavailableReason.NotInterpreted,
                    "inconsistent" => TrackNormalizationUnavailableReason.Inconsistent,
                    null => null,
                    _ => throw new InvalidOperationException(
                        "Unsupported track normalization reason."
                    ),
                },
                value.Normalization.Parts.Select(Part).ToArray()
            ),
        };

    public static ImportedVenueResource ImportedVenue(CatalogImportedVenue value) =>
        new()
        {
            Id = new(value.uuid),
            ArtistId = new(value.artist_uuid),
            Latitude = value.latitude,
            Longitude = value.longitude,
            Name = value.name,
            Location = value.location,
            UpstreamIdentifier = value.upstream_identifier,
            Slug = value.slug,
            PastNames = value.past_names,
            CreatedAt = value.created_at,
            UpdatedAt = value.updated_at,
        };

    public static LinkResource Link(CatalogLink value) =>
        new()
        {
            SourceId = new(value.source_uuid),
            Provider = value.provider,
            ForReviews = value.for_reviews,
            ForRatings = value.for_ratings,
            ForSource = value.for_source,
            Url = value.url,
            Label = value.label,
            CreatedAt = value.created_at,
            UpdatedAt = value.updated_at,
        };

    public static EraResource Era(CatalogEra value) =>
        new() { Order = value.order, Name = value.name };

    public static SongResource Song(CatalogSong value) =>
        new()
        {
            Id = SongId.Parse(value.id),
            ArtistId = new(value.artist_uuid),
            Name = value.name,
            Kind = value.kind is { } kind ? NormalizationEnums.Parse<SongKind>(kind) : null,
        };

    public static VenueResource Venue(CatalogVenue value) =>
        new()
        {
            Id = VenueId.Parse(value.id),
            Name = value.name,
            Kind = NormalizationEnums.Parse<VenueKind>(value.kind),
            ParentId = value.parent_id is { } parent ? VenueId.Parse(parent) : null,
            City = value.city,
            Region = value.region,
            Country = value.country,
            Latitude = value.latitude,
            Longitude = value.longitude,
            CoordinatePrecision = value.coordinate_precision is { } precision
                ? NormalizationEnums.Parse<VenueCoordinatePrecision>(precision)
                : null,
            Names = value.Names.Select(VenueName).ToArray(),
        };

    private static VenueNameResource VenueName(CatalogVenueName value) =>
        new(value.name, NormalizationEnums.Parse<VenueNameKind>(value.kind));

    public static TrackPartResource Part(CatalogPart value) =>
        new()
        {
            Ordinal = value.part,
            Text = value.text,
            Qualifiers = value
                .qualifiers.Select(NormalizationEnums.Parse<TrackQualifier>)
                .ToArray(),
            Notes = value.notes,
            SegueAfter = value.segue_after,
            Song = value.Song is { } song ? Song(song) : null,
            State =
                !value.resolved ? TrackPartState.Unresolved
                : value.Song is not null ? TrackPartState.Matched
                : TrackPartState.NonSong,
        };

    public static YearResource Year(CatalogYear value) => ProjectYear<YearResource>(value);

    public static YearWithShowsResource YearWithShows(CatalogYearDetail value) =>
        ProjectYear<YearWithShowsResource>(value.Year) with
        {
            Shows = value.Shows.Select(ShowSummary).ToArray(),
        };

    private static T ProjectYear<T>(CatalogYear value)
        where T : YearResource, new() =>
        new()
        {
            Id = new(value.uuid),
            ArtistId = new(value.artist_uuid),
            ShowCount = value.show_count,
            SourceCount = value.source_count,
            Duration = value.duration,
            AvgDuration = value.avg_duration,
            AvgRating = value.avg_rating,
            Year = value.year,
            CreatedAt = value.created_at,
            UpdatedAt = value.updated_at,
        };

    public static TourResource Tour(CatalogTour value) =>
        new()
        {
            Id = new(value.uuid),
            ArtistId = new(value.artist_uuid),
            StartDate = value.start_date,
            EndDate = value.end_date,
            Name = value.name,
            Slug = value.slug,
            UpstreamIdentifier = value.upstream_identifier,
            CreatedAt = value.created_at,
            UpdatedAt = value.updated_at,
        };
}
