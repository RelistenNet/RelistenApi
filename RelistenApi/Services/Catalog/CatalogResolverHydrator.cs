using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;

namespace Relisten.Catalog;

/// <summary>
/// Batch-hydrates available resolver targets in the lookup's snapshot. Recording
/// parents are shallow patches, so resolving one file cannot clear sibling files.
/// </summary>
public sealed class CatalogResolverHydrator(
    CatalogBrowseReader browse,
    CatalogPopularityMetadataReader popularity,
    CatalogShowMetadataReader showMetadata,
    CatalogTourMetadataReader tourMetadata,
    CatalogVenueMetadataReader venueMetadata,
    CatalogSongReader songMetadata,
    CatalogEntityReader entities
)
{
    public async Task<CatalogResolvedEntities> Read(
        CatalogReadSession session,
        IReadOnlyList<CatalogResolvedTarget> targets,
        CancellationToken cancellationToken
    )
    {
        if (targets.Count == 0)
        {
            return new();
        }

        var tracks = await CatalogEntityReader.ReadTracks(
            session,
            Uuids(targets, CatalogReferenceKind.Track),
            cancellationToken
        );
        var sets = await CatalogEntityReader.ReadSets(
            session,
            Uuids(targets, CatalogReferenceKind.SourceSet)
                .Concat(tracks.Select(track => track.source_set_uuid))
                .Distinct()
                .ToArray(),
            cancellationToken
        );
        var reviews = await CatalogEntityReader.ReadReviews(
            session,
            Uuids(targets, CatalogReferenceKind.SourceReview),
            cancellationToken
        );
        var sources = await CatalogEntityReader.ReadSources(
            session,
            Uuids(targets, CatalogReferenceKind.Source)
                .Concat(sets.Select(set => set.source_uuid))
                .Concat(reviews.Select(review => review.source_uuid))
                .Distinct()
                .ToArray(),
            cancellationToken
        );
        var shows = await CatalogEntityReader.ReadShows(
            session,
            Uuids(targets, CatalogReferenceKind.Show)
                .Concat(sources.Select(source => source.show_uuid))
                .Distinct()
                .ToArray(),
            cancellationToken
        );
        await showMetadata.Apply(session, shows, cancellationToken);
        await entities.ApplyTrackNormalization(session, tracks, cancellationToken);

        var setlistShows = await CatalogEntityReader.ReadSetlistShows(
            session,
            Uuids(targets, CatalogReferenceKind.SetlistShow),
            cancellationToken
        );
        var importedVenues = await CatalogEntityReader.ReadImportedVenues(
            session,
            Uuids(targets, CatalogReferenceKind.ImportedVenue),
            sources
                .Select(source => source.venue_id)
                .OfType<long>()
                .Concat(setlistShows.Select(show => show.venue_id))
                .Distinct()
                .ToArray(),
            cancellationToken
        );
        var venuesById = importedVenues.ToDictionary(venue => venue.id);
        foreach (var source in sources)
        {
            source.ImportedVenue = source.venue_id is { } venueId
                ? venuesById.GetValueOrDefault(venueId)
                : null;
        }

        var artistVenues = await CatalogEntityReader.ReadArtistVenues(
            session,
            Ids(targets, CatalogReferenceKind.ArtistVenue),
            cancellationToken
        );
        var venues = await venueMetadata.Read(
            session,
            Ids(targets, CatalogReferenceKind.Venue)
                .Concat(artistVenues.Select(relationship => relationship.venue_id))
                .Distinct()
                .ToArray(),
            cancellationToken
        );
        foreach (var relationship in artistVenues)
        {
            relationship.Venue = venues[relationship.venue_id];
        }

        var years = await CatalogEntityReader.ReadYears(
            session,
            Uuids(targets, CatalogReferenceKind.Year),
            cancellationToken
        );
        await popularity.ApplyYears(session, years, cancellationToken);
        var tours = await tourMetadata.Read(
            session,
            Uuids(targets, CatalogReferenceKind.Tour)
                .Concat(setlistShows.Select(show => show.tour_uuid).OfType<Guid>())
                .Distinct()
                .ToArray(),
            cancellationToken
        );
        var songs = Ids(targets, CatalogReferenceKind.Song);
        var importedSongs = await entities.ReadImportedSongs(
            session,
            Uuids(targets, CatalogReferenceKind.ImportedSong),
            cancellationToken
        );

        return new()
        {
            // Mobile caches the artist catalog separately. Only explicitly requested
            // artists belong here; recording parents do not duplicate that catalog.
            Artists = await browse.ReadArtists(
                session,
                Uuids(targets, CatalogReferenceKind.Artist),
                cancellationToken
            ),
            Shows = shows,
            Sources = sources,
            SourceSets = sets,
            Tracks = tracks,
            Years = years
                .Concat(shows.Select(show => show.Year).OfType<CatalogYear>())
                .DistinctBy(year => year.uuid)
                .OrderBy(year => year.uuid)
                .ToArray(),
            Tours = tours
                .Values.Concat(shows.Select(show => show.Tour).OfType<CatalogTour>())
                .DistinctBy(tour => tour.uuid)
                .OrderBy(tour => tour.uuid)
                .ToArray(),
            ImportedVenues = importedVenues
                .Concat(shows.Select(show => show.ImportedVenue).OfType<CatalogImportedVenue>())
                .DistinctBy(venue => venue.uuid)
                .OrderBy(venue => venue.uuid)
                .ToArray(),
            ImportedSongs = importedSongs,
            Venues = venues
                .Values.Concat(shows.Select(show => show.ArtistVenue?.Venue).OfType<CatalogVenue>())
                .DistinctBy(venue => venue.id)
                .OrderBy(venue => venue.id, StringComparer.Ordinal)
                .ToArray(),
            Songs =
                songs.Length == 0
                    ? []
                    : (await songMetadata.ReadSongDetails(session, songs, cancellationToken))
                        .OrderBy(song => song.id, StringComparer.Ordinal)
                        .ToArray(),
            ArtistVenues = artistVenues
                .Concat(shows.Select(show => show.ArtistVenue).OfType<CatalogArtistVenue>())
                .DistinctBy(relationship => relationship.id)
                .OrderBy(relationship => relationship.id, StringComparer.Ordinal)
                .ToArray(),
            SourceReviews = reviews,
            SetlistShows = setlistShows,
        };
    }

    private static Guid[] Uuids(
        IReadOnlyList<CatalogResolvedTarget> targets,
        CatalogReferenceKind kind
    ) =>
        targets
            .Where(target => target.Kind == kind)
            .Select(target => target.Uuid!.Value)
            .Distinct()
            .ToArray();

    private static string[] Ids(
        IReadOnlyList<CatalogResolvedTarget> targets,
        CatalogReferenceKind kind
    ) =>
        targets
            .Where(target => target.Kind == kind)
            .Select(target => target.Id)
            .Distinct()
            .ToArray();
}
