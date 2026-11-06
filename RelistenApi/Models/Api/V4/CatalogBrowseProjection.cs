using System.Linq;
using Relisten.Catalog;

namespace Relisten.Api.Models.Api.V4;

/// <summary>Projects browse metadata without issuing additional catalog queries.</summary>
public static class CatalogBrowseProjection
{
    public static ArtistResource Artist(CatalogArtist value) =>
        ProjectArtist<ArtistResource>(value);

    internal static T ProjectArtist<T>(CatalogArtist value)
        where T : ArtistResource, new() =>
        new()
        {
            Id = new(value.uuid),
            Name = value.name,
            Slug = value.slug,
            SortName = value.sort_name,
            MusicbrainzId = value.musicbrainz_id,
            Featured = value.featured,
            ShowCount = value.show_count,
            SourceCount = value.source_count,
            Popularity = value.Popularity is { } metrics
                ? V4CatalogProjection.ProjectPopularity(metrics)
                : null,
            CreatedAt = value.created_at,
            UpdatedAt = value.updated_at,
            Features = Features(value.Features).ToString("D"),
            UpstreamSources = value.UpstreamSources.Select(Provider).ToArray(),
        };

    private static ArtistFeatures Features(CatalogFeatures value) =>
        (value.descriptions ? ArtistFeatures.Descriptions : ArtistFeatures.None)
        | (value.eras ? ArtistFeatures.Eras : ArtistFeatures.None)
        | (value.multiple_sources ? ArtistFeatures.MultipleSources : ArtistFeatures.None)
        | (value.reviews ? ArtistFeatures.Reviews : ArtistFeatures.None)
        | (value.ratings ? ArtistFeatures.Ratings : ArtistFeatures.None)
        | (value.tours ? ArtistFeatures.Tours : ArtistFeatures.None)
        | (value.taper_notes ? ArtistFeatures.TaperNotes : ArtistFeatures.None)
        | (value.source_information ? ArtistFeatures.SourceInformation : ArtistFeatures.None)
        | (value.sets ? ArtistFeatures.Sets : ArtistFeatures.None)
        | (value.per_show_venues ? ArtistFeatures.PerShowVenues : ArtistFeatures.None)
        | (value.per_source_venues ? ArtistFeatures.PerSourceVenues : ArtistFeatures.None)
        | (value.venue_coords ? ArtistFeatures.VenueCoords : ArtistFeatures.None)
        | (value.songs ? ArtistFeatures.Songs : ArtistFeatures.None)
        | (value.years ? ArtistFeatures.Years : ArtistFeatures.None)
        | (value.track_md5s ? ArtistFeatures.TrackMd5s : ArtistFeatures.None)
        | (value.review_titles ? ArtistFeatures.ReviewTitles : ArtistFeatures.None)
        | (value.jam_charts ? ArtistFeatures.JamCharts : ArtistFeatures.None)
        | (
            value.setlist_data_incomplete
                ? ArtistFeatures.SetlistDataIncomplete
                : ArtistFeatures.None
        )
        | (value.track_names ? ArtistFeatures.TrackNames : ArtistFeatures.None)
        | (value.venue_past_names ? ArtistFeatures.VenuePastNames : ArtistFeatures.None)
        | (value.reviews_have_ratings ? ArtistFeatures.ReviewsHaveRatings : ArtistFeatures.None)
        | (value.track_durations ? ArtistFeatures.TrackDurations : ArtistFeatures.None)
        | (value.can_have_flac ? ArtistFeatures.CanHaveFlac : ArtistFeatures.None)
        | (value.norm_catalogue ? ArtistFeatures.Normalization : ArtistFeatures.None);

    private static ArtistProviderResource Provider(CatalogProviderAssignment value) =>
        new()
        {
            Provider = UpstreamProviders.TryParse(value.provider, out var provider)
                ? provider
                : null,
            UpstreamIdentifier = value.upstream_identifier,
        };
}
