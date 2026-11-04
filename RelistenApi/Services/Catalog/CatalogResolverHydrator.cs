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
    CatalogTrackNormalizationReader trackNormalization,
    CatalogTourMetadataReader tourMetadata,
    CatalogVenueMetadataReader venueMetadata,
    CatalogSongReader songMetadata
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

        var tracks = await ReadTracks(
            session,
            Uuids(targets, CatalogReferenceKind.Track),
            cancellationToken
        );
        var sets = await ReadSets(
            session,
            Uuids(targets, CatalogReferenceKind.SourceSet)
                .Concat(tracks.Select(track => track.source_set_uuid))
                .Distinct()
                .ToArray(),
            cancellationToken
        );
        var reviews = await ReadReviews(
            session,
            Uuids(targets, CatalogReferenceKind.SourceReview),
            cancellationToken
        );
        var sources = await ReadSources(
            session,
            Uuids(targets, CatalogReferenceKind.Source)
                .Concat(sets.Select(set => set.source_uuid))
                .Concat(reviews.Select(review => review.source_uuid))
                .Distinct()
                .ToArray(),
            cancellationToken
        );
        var shows = await ReadShows(
            session,
            Uuids(targets, CatalogReferenceKind.Show)
                .Concat(sources.Select(source => source.show_uuid))
                .Distinct()
                .ToArray(),
            cancellationToken
        );
        await showMetadata.Apply(session, shows, cancellationToken);
        await ApplyTrackNormalization(session, tracks, cancellationToken);

        var setlistShows = await ReadSetlistShows(
            session,
            Uuids(targets, CatalogReferenceKind.SetlistShow),
            cancellationToken
        );
        var importedVenues = await ReadImportedVenues(
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

        var artistVenues = await ReadArtistVenues(
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

        var years = await ReadYears(
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
        var importedSongs = await ReadImportedSongs(
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

    private async Task ApplyTrackNormalization(
        CatalogReadSession session,
        CatalogTrack[] tracks,
        CancellationToken cancellationToken
    )
    {
        if (tracks.Length == 0)
        {
            return;
        }
        var approved = (
            await session.Connection.QueryAsync<Guid>(
                session.Command(
                    """
                    SELECT a.uuid FROM artists a JOIN features f ON f.artist_id=a.id
                    WHERE a.uuid=ANY(@artists) AND f.norm_catalogue
                    """,
                    new
                    {
                        artists = tracks.Select(track => track.artist_uuid).Distinct().ToArray(),
                    },
                    cancellationToken
                )
            )
        ).ToHashSet();
        foreach (var group in tracks.GroupBy(track => approved.Contains(track.artist_uuid)))
        {
            await trackNormalization.Apply(session, group.ToArray(), group.Key, cancellationToken);
        }
    }

    private static async Task<CatalogTrack[]> ReadTracks(
        CatalogReadSession session,
        Guid[] uuids,
        CancellationToken cancellationToken
    ) =>
        (
            await session.Connection.QueryAsync<CatalogTrack>(
                session.Command(
                    CatalogTrackNormalizationReader.TrackSelect
                        + " WHERE t.uuid=ANY(@uuids) AND NOT t.is_orphaned ORDER BY t.uuid",
                    new { uuids },
                    cancellationToken
                )
            )
        ).ToArray();

    private static async Task<CatalogSet[]> ReadSets(
        CatalogReadSession session,
        Guid[] uuids,
        CancellationToken cancellationToken
    ) =>
        (
            await session.Connection.QueryAsync<CatalogSet>(
                session.Command(
                    """
                    SELECT sets.*,src.uuid source_uuid,a.uuid artist_uuid
                    FROM source_sets sets JOIN sources src ON src.id=sets.source_id
                    JOIN artists a ON a.id=src.artist_id
                    WHERE sets.uuid=ANY(@uuids) ORDER BY sets.uuid
                    """,
                    new { uuids },
                    cancellationToken
                )
            )
        ).ToArray();

    private static async Task<CatalogSourceReview[]> ReadReviews(
        CatalogReadSession session,
        Guid[] uuids,
        CancellationToken cancellationToken
    ) =>
        (
            await session.Connection.QueryAsync<CatalogSourceReview>(
                session.Command(
                    """
                    SELECT r.uuid,src.uuid source_uuid,r.rating,r.title,r.review,r.author,r.created_at,r.updated_at
                    FROM source_reviews r JOIN sources src ON src.id=r.source_id
                    WHERE r.uuid=ANY(@uuids) ORDER BY r.uuid
                    """,
                    new { uuids },
                    cancellationToken
                )
            )
        ).ToArray();

    private static async Task<CatalogSource[]> ReadSources(
        CatalogReadSession session,
        Guid[] uuids,
        CancellationToken cancellationToken
    ) =>
        (
            await session.Connection.QueryAsync<CatalogSource>(
                session.Command(
                    CatalogRecordingReader.SourceSelect
                        + " WHERE src.uuid=ANY(@uuids) ORDER BY src.uuid",
                    new { uuids },
                    cancellationToken
                )
            )
        ).ToArray();

    private static async Task<CatalogShow[]> ReadShows(
        CatalogReadSession session,
        Guid[] uuids,
        CancellationToken cancellationToken
    ) =>
        (
            await session.Connection.QueryAsync<CatalogShow>(
                session.Command(
                    CatalogShowMetadataReader.ShowSelect
                        + " WHERE s.uuid=ANY(@uuids) ORDER BY s.uuid",
                    new { uuids },
                    cancellationToken
                )
            )
        ).ToArray();

    private static async Task<CatalogSetlistShow[]> ReadSetlistShows(
        CatalogReadSession session,
        Guid[] uuids,
        CancellationToken cancellationToken
    ) =>
        (
            await session.Connection.QueryAsync<CatalogSetlistShow>(
                session.Command(
                    """
                    SELECT ss.uuid,a.uuid artist_uuid,ss.venue_id,v.uuid imported_venue_uuid,t.uuid tour_uuid,
                           ss.era_id,e."order" era_order,e.name era_name,ss.date::timestamp date,
                           ss.upstream_identifier,ss.created_at,ss.updated_at
                    FROM setlist_shows ss JOIN artists a ON a.id=ss.artist_id
                    JOIN venues v ON v.id=ss.venue_id
                    LEFT JOIN tours t ON t.id=ss.tour_id LEFT JOIN eras e ON e.id=ss.era_id
                    WHERE ss.uuid=ANY(@uuids) ORDER BY ss.uuid
                    """,
                    new { uuids },
                    cancellationToken
                )
            )
        ).ToArray();

    private static async Task<CatalogImportedVenue[]> ReadImportedVenues(
        CatalogReadSession session,
        Guid[] uuids,
        long[] venueIds,
        CancellationToken cancellationToken
    ) =>
        (
            await session.Connection.QueryAsync<CatalogImportedVenue>(
                session.Command(
                    CatalogImportedVenueMetadataReader.Select
                        + " WHERE v.uuid=ANY(@uuids) OR v.id=ANY(@venueIds) ORDER BY v.uuid",
                    new { uuids, venueIds },
                    cancellationToken
                )
            )
        ).ToArray();

    private static async Task<CatalogArtistVenue[]> ReadArtistVenues(
        CatalogReadSession session,
        string[] ids,
        CancellationToken cancellationToken
    ) =>
        (
            await session.Connection.QueryAsync<CatalogArtistVenue>(
                session.Command(
                    """
                    SELECT av.id,av.artist_uuid,av.venue_id,av.artist_show_count,av.created_at,av.updated_at
                    FROM norm.artist_venues av WHERE av.id=ANY(@ids) ORDER BY av.id
                    """,
                    new { ids },
                    cancellationToken
                )
            )
        ).ToArray();

    private static async Task<CatalogYear[]> ReadYears(
        CatalogReadSession session,
        Guid[] uuids,
        CancellationToken cancellationToken
    ) =>
        (
            await session.Connection.QueryAsync<CatalogYear>(
                session.Command(
                    """
                    SELECT y.*,a.uuid artist_uuid FROM years y JOIN artists a ON a.id=y.artist_id
                    WHERE y.uuid=ANY(@uuids) ORDER BY y.uuid
                    """,
                    new { uuids },
                    cancellationToken
                )
            )
        ).ToArray();

    private static async Task<CatalogImportedSong[]> ReadImportedSongs(
        CatalogReadSession session,
        Guid[] uuids,
        CancellationToken cancellationToken
    ) =>
        (
            await session.Connection.QueryAsync<CatalogImportedSong>(
                session.Command(
                    CatalogSongReader.ImportedSongSelect
                        + " WHERE s.uuid=ANY(@uuids) ORDER BY s.uuid",
                    new { uuids },
                    cancellationToken
                )
            )
        ).ToArray();

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
