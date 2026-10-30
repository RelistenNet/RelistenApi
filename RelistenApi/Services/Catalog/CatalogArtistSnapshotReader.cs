using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Relisten.Catalog;

/// <summary>
/// Composes the same artist collections used by navigation in one read snapshot.
/// The snapshot owns collection membership, not globally shared entity lifetimes.
/// </summary>
public sealed class CatalogArtistSnapshotReader(
    DbService db,
    CatalogBrowseReader browse,
    CatalogTourReader tours,
    CatalogVenueReader venueBrowse,
    CatalogSongReader songBrowse,
    CatalogArtistVenueMetadataReader artistVenueMetadata,
    CatalogVenueMetadataReader venueMetadata
)
{
    public async Task<CatalogArtistSnapshot?> Read(
        Guid artistUuid,
        CancellationToken cancellationToken = default
    )
    {
        await using var session = await CatalogReadSession.Open(db, cancellationToken);
        var artist = await browse.ReadArtist(session, artistUuid, cancellationToken);
        if (artist is null)
        {
            return null;
        }

        // The owner cannot disappear between these reads: all collection readers
        // use this same repeatable-read session rather than opening their own.
        var years = (await browse.ReadYears(session, artistUuid, cancellationToken))!;
        var shows = (await browse.ReadShows(session, artistUuid, cancellationToken))!;
        var artistTours = (await tours.ReadTours(session, artistUuid, cancellationToken))!;
        var eras = (await browse.ReadEras(session, artistUuid, cancellationToken))!;
        var importedVenues = (
            await venueBrowse.ReadImportedVenues(session, artistUuid, cancellationToken)
        )!;
        var importedSongs = (
            await songBrowse.ReadImportedSongs(session, artistUuid, cancellationToken)
        )!;
        var venueEntries = (
            await venueBrowse.ReadArtistVenues(session, artistUuid, cancellationToken)
        )!;
        var songEntries = (
            await songBrowse.ReadArtistSongs(session, artistUuid, cancellationToken)
        )!;
        var artistVenues = await ReadArtistVenues(session, artistUuid, cancellationToken);
        var venues = artistVenues
            .Select(relationship => relationship.Venue)
            .DistinctBy(venue => venue.id)
            .OrderBy(venue => venue.id, StringComparer.Ordinal)
            .ToArray();
        var songs = songEntries
            .Select(entry => entry.Song)
            .OfType<CatalogSongDetail>()
            .OrderBy(song => song.id, StringComparer.Ordinal)
            .ToArray();

        return new(
            artist,
            years,
            shows,
            artistTours,
            eras,
            importedVenues,
            importedSongs,
            venueEntries,
            songEntries,
            artistVenues,
            venues,
            songs
        );
    }

    private async Task<IReadOnlyList<CatalogArtistVenue>> ReadArtistVenues(
        CatalogReadSession session,
        Guid artistUuid,
        CancellationToken cancellationToken
    )
    {
        var relationships = await artistVenueMetadata.Read(
            session,
            [artistUuid],
            null,
            cancellationToken
        );
        var venues = await venueMetadata.Read(
            session,
            relationships.Values.Select(relationship => relationship.venue_id).ToArray(),
            cancellationToken
        );

        // Retained zero-count rows must refresh cached counts too. A merged venue
        // cannot rebind its old relationship ID to a different canonical place.
        return relationships
            .Values.Where(relationship =>
                venues.TryGetValue(relationship.venue_id, out var venue)
                && venue.id == relationship.venue_id
            )
            .Select(relationship =>
            {
                relationship.Venue = venues[relationship.venue_id];
                return relationship;
            })
            .OrderBy(relationship => relationship.id, StringComparer.Ordinal)
            .ToArray();
    }
}
