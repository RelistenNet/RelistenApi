using System.Linq;
using Relisten.Catalog;

namespace Relisten.Api.Models.Api.V4;

/// <summary>Projects browse metadata without issuing additional catalog queries.</summary>
public static class CatalogBrowseProjection
{
    public static ArtistResource Artist(CatalogArtist value) =>
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
            CreatedAt = value.created_at,
            UpdatedAt = value.updated_at,
            Features = Features(value.Features),
            UpstreamSources = value.UpstreamSources.Select(Provider).ToArray(),
        };

    private static ArtistFeaturesResource Features(CatalogFeatures value) =>
        new()
        {
            Descriptions = value.descriptions,
            Eras = value.eras,
            MultipleSources = value.multiple_sources,
            Reviews = value.reviews,
            Ratings = value.ratings,
            Tours = value.tours,
            TaperNotes = value.taper_notes,
            SourceInformation = value.source_information,
            Sets = value.sets,
            PerShowVenues = value.per_show_venues,
            PerSourceVenues = value.per_source_venues,
            VenueCoords = value.venue_coords,
            Songs = value.songs,
            Years = value.years,
            TrackMd5s = value.track_md5s,
            ReviewTitles = value.review_titles,
            JamCharts = value.jam_charts,
            SetlistDataIncomplete = value.setlist_data_incomplete,
            TrackNames = value.track_names,
            VenuePastNames = value.venue_past_names,
            ReviewsHaveRatings = value.reviews_have_ratings,
            TrackDurations = value.track_durations,
            CanHaveFlac = value.can_have_flac,
            Normalization = value.norm_catalogue,
        };

    private static ArtistProviderResource Provider(CatalogProviderAssignment value) =>
        new()
        {
            Provider = value.provider,
            UpstreamIdentifier = value.upstream_identifier,
            Url = value.url,
            Description = value.description,
            CreditLine = value.credit_line,
        };
}
