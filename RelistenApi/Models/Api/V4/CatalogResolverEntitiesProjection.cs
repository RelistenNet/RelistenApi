using System.Linq;
using Relisten.Catalog;
using Relisten.Identifiers;

namespace Relisten.Api.Models.Api.V4;

public static partial class V4CatalogProjection
{
    public static CatalogResolverEntitiesResource ResolvedEntities(
        CatalogResolvedEntities entities
    ) =>
        new()
        {
            Artists = entities.Artists.Select(CatalogBrowseProjection.Artist).ToArray(),
            Shows = entities.Shows.Select(ShowSummary).ToArray(),
            Sources = entities.Sources.Select(SourceSummary).ToArray(),
            SourceSets = entities.SourceSets.Select(SetSummary).ToArray(),
            Tracks = entities.Tracks.Select(Track).ToArray(),
            Years = entities.Years.Select(Year).ToArray(),
            Tours = entities.Tours.Select(Tour).ToArray(),
            ImportedVenues = entities.ImportedVenues.Select(ImportedVenue).ToArray(),
            ImportedSongs = entities.ImportedSongs.Select(ImportedSong).ToArray(),
            Venues = entities.Venues.Select(Venue).ToArray(),
            Songs = entities.Songs.Select(SongDetail).ToArray(),
            ArtistVenues = entities.ArtistVenues.Select(ArtistVenue).ToArray(),
            SourceReviews = entities.SourceReviews.Select(SourceReview).ToArray(),
            SetlistShows = entities.SetlistShows.Select(SetlistShow).ToArray(),
        };

    private static SetlistShowResource SetlistShow(CatalogSetlistShow show) =>
        new()
        {
            Id = new(show.uuid),
            ArtistId = new(show.artist_uuid),
            Date = show.date,
            ImportedVenueId = new(show.imported_venue_uuid),
            TourId = show.tour_uuid is { } tour ? new TourId(tour) : null,
            Era = show.era_id.HasValue
                ? new() { Order = show.era_order ?? 0, Name = show.era_name! }
                : null,
            UpstreamIdentifier = show.upstream_identifier,
            CreatedAt = show.created_at,
            UpdatedAt = show.updated_at,
        };
}
