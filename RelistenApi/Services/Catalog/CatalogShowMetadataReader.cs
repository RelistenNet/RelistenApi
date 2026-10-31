using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;

namespace Relisten.Catalog;

/// <summary>
/// Fills show metadata in batches for both navigation summaries and full recording reads.
/// The caller owns the snapshot. This reader never loads sources or tracks itself.
/// </summary>
public sealed class CatalogShowMetadataReader(
    CatalogImportedVenueMetadataReader importedVenueMetadata,
    CatalogVenueMetadataReader venueMetadata,
    CatalogArtistVenueMetadataReader artistVenueMetadata,
    CatalogTourMetadataReader tourMetadata,
    CatalogShowPopularityReader popularity
)
{
    public async Task Apply(
        CatalogReadSession session,
        IReadOnlyList<CatalogShow> shows,
        CancellationToken cancellationToken
    )
    {
        if (shows.Count == 0)
        {
            return;
        }

        var venueIds = shows
            .Select(x => x.venue_id)
            .Concat(shows.SelectMany(x => x.Sources).Select(x => x.venue_id))
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .Distinct()
            .ToArray();
        var venues = (
            await importedVenueMetadata.Read(session, venueIds, cancellationToken)
        ).ToDictionary(x => x.id);
        var yearUuids = shows
            .Where(x => x.year_uuid.HasValue)
            .Select(x => x.year_uuid!.Value)
            .Distinct()
            .ToArray();
        var years = (
            await session.Connection.QueryAsync<CatalogYear>(
                session.Command(
                    "SELECT y.*,a.uuid artist_uuid FROM years y JOIN artists a ON a.id=y.artist_id WHERE y.uuid=ANY(@yearUuids)",
                    new { yearUuids },
                    cancellationToken
                )
            )
        ).ToDictionary(x => x.uuid);
        var tourUuids = shows
            .Where(x => x.tour_uuid.HasValue)
            .Select(x => x.tour_uuid!.Value)
            .Distinct()
            .ToArray();
        var tours = await tourMetadata.Read(session, tourUuids, cancellationToken);
        var showUuids = shows.Select(x => x.uuid).ToArray();
        var eras = (
            await session.Connection.QueryAsync<ShowEra>(
                session.Command(
                    "SELECT s.uuid show_uuid,e.\"order\",e.name FROM eras e JOIN shows s ON s.era_id=e.id WHERE s.uuid=ANY(@showUuids)",
                    new { showUuids },
                    cancellationToken
                )
            )
        ).ToDictionary(x => x.show_uuid);

        foreach (var show in shows)
        {
            show.ImportedVenue = show.venue_id is { } venueId
                ? venues.GetValueOrDefault(venueId)
                : null;
            show.Year = show.year_uuid is { } yearId ? years.GetValueOrDefault(yearId) : null;
            show.Tour = show.tour_uuid is { } tourId ? tours.GetValueOrDefault(tourId) : null;
            show.Era = eras.TryGetValue(show.uuid, out var era)
                ? new() { order = era.order, name = era.name }
                : null;
            foreach (var source in show.Sources)
            {
                source.ImportedVenue = source.venue_id is { } sourceVenueId
                    ? venues.GetValueOrDefault(sourceVenueId)
                    : null;
            }
        }

        await ApplyVenues(session, shows, cancellationToken);
        await popularity.Apply(shows, cancellationToken);
    }

    private async Task ApplyVenues(
        CatalogReadSession session,
        IReadOnlyList<CatalogShow> shows,
        CancellationToken cancellationToken
    )
    {
        var approvedShows = shows.Where(x => x.normalization_enabled).ToDictionary(x => x.uuid);
        if (approvedShows.Count == 0)
        {
            return;
        }

        // Read show-specific assignments before sharing venue resolution: one imported
        // venue can map differently for separate shows, even within the same artist.
        var assignments = (
            await session.Connection.QueryAsync<ShowVenue>(
                session.Command(
                    "SELECT show_uuid,venue_id FROM norm.show_venues WHERE show_uuid=ANY(@showUuids)",
                    new { showUuids = approvedShows.Keys.ToArray() },
                    cancellationToken
                )
            )
        ).ToArray();
        var resolved = await venueMetadata.Read(
            session,
            assignments.Select(assignment => assignment.venue_id).ToArray(),
            cancellationToken
        );
        var canonicalIds = resolved.Values.Select(venue => venue.id).Distinct().ToArray();
        var relationships = await artistVenueMetadata.Read(
            session,
            approvedShows.Values.Select(show => show.artist_uuid).Distinct().ToArray(),
            canonicalIds,
            cancellationToken
        );
        foreach (var assignment in assignments)
        {
            var show = approvedShows[assignment.show_uuid];
            if (
                resolved.TryGetValue(assignment.venue_id, out var venue)
                && relationships.TryGetValue((show.artist_uuid, venue.id), out var relationship)
            )
            {
                relationship.Venue = venue;
                show.ArtistVenue = relationship;
            }
        }
    }

    internal const string ShowSelect = """
        SELECT s.id,s.uuid,s.venue_id,s.date::timestamp date,s.avg_rating,s.avg_duration,
               s.display_date,s.created_at,s.updated_at,a.uuid artist_uuid,y.uuid year_uuid,t.uuid tour_uuid,
               COALESCE(f.norm_catalogue,false) normalization_enabled,
               COALESCE(cnt.source_count,0)::int source_count,
               COALESCE(cnt.has_soundboard_source,false) has_soundboard_source,
               COALESCE(cnt.has_flac,false) has_streamable_flac_source,
               COALESCE(cnt.max_updated_at,s.updated_at) most_recent_source_updated_at
        FROM shows s JOIN artists a ON a.id=s.artist_id
        LEFT JOIN features f ON f.artist_id=a.id
        LEFT JOIN years y ON y.id=s.year_id LEFT JOIN tours t ON t.id=s.tour_id
        LEFT JOIN show_source_information cnt ON cnt.show_id=s.id
        """;

    private sealed class ShowEra
    {
        public Guid show_uuid { get; set; }
        public int order { get; set; }
        public string name { get; set; } = null!;
    }

    private sealed class ShowVenue
    {
        public Guid show_uuid { get; set; }
        public string venue_id { get; set; } = null!;
    }
}
