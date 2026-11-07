using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;

namespace Relisten.Catalog;

/// <summary>Shared entity batches used by reference hydration and catalog graph reads.
/// The caller owns the snapshot; these methods return materialized facts only.</summary>
public sealed class CatalogEntityReader(
    CatalogTrackNormalizationReader trackNormalization,
    CatalogImportedSongMappingReader importedSongMappings,
    CatalogBrowseReader browse,
    CatalogShowMetadataReader showMetadata,
    CatalogPopularityMetadataReader popularity,
    CatalogSongReader songs,
    CatalogTourMetadataReader tours,
    CatalogVenueMetadataReader venues,
    CatalogRecordingReader recordings
)
{
    /// <summary>Hydrates only explicitly selected targets, without fetching their parents.</summary>
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
        var kind = targets[0].Kind;
        if (targets.Any(t => t.Kind != kind))
        {
            throw new ArgumentException("An entity batch must have one kind.", nameof(targets));
        }
        var uuids = targets
            .Where(t => t.Uuid.HasValue)
            .Select(t => t.Uuid!.Value)
            .Distinct()
            .ToArray();
        var ids = targets.Select(t => t.Id).Distinct().ToArray();
        switch (kind)
        {
            case CatalogReferenceKind.Artist:
                return new()
                {
                    Artists = await browse.ReadArtists(session, uuids, cancellationToken),
                };
            case CatalogReferenceKind.Show:
                var showRows = await ReadShows(session, uuids, cancellationToken);
                await showMetadata.Apply(session, showRows, cancellationToken);
                return new() { Shows = showRows };
            case CatalogReferenceKind.Track:
                var trackRows = await ReadTracks(session, uuids, cancellationToken);
                await ApplyTrackNormalization(session, trackRows, cancellationToken);
                return new() { Tracks = trackRows };
            case CatalogReferenceKind.Source:
                var sourceRows = await ReadSources(session, uuids, cancellationToken);
                var imported = await ReadImportedVenues(
                    session,
                    [],
                    sourceRows.Select(s => s.venue_id).OfType<long>().Distinct().ToArray(),
                    cancellationToken
                );
                var byVenue = imported.ToDictionary(v => v.id);
                foreach (var source in sourceRows)
                {
                    source.ImportedVenue = source.venue_id is { } id
                        ? byVenue.GetValueOrDefault(id)
                        : null;
                }
                return new() { Sources = sourceRows };
            case CatalogReferenceKind.SourceSet:
                return new() { SourceSets = await ReadSets(session, uuids, cancellationToken) };
            case CatalogReferenceKind.SourceReview:
                return new()
                {
                    SourceReviews = await ReadReviews(session, uuids, cancellationToken),
                };
            case CatalogReferenceKind.Year:
                var yearRows = await ReadYears(session, uuids, cancellationToken);
                await popularity.ApplyYears(session, yearRows, cancellationToken);
                return new() { Years = yearRows };
            case CatalogReferenceKind.Tour:
                return new()
                {
                    Tours = (await tours.Read(session, uuids, cancellationToken)).Values.ToArray(),
                };
            case CatalogReferenceKind.ImportedVenue:
                return new()
                {
                    ImportedVenues = await ReadImportedVenues(
                        session,
                        uuids,
                        [],
                        cancellationToken
                    ),
                };
            case CatalogReferenceKind.ImportedSong:
                return new()
                {
                    ImportedSongs = await ReadImportedSongs(session, uuids, cancellationToken),
                };
            case CatalogReferenceKind.ArtistVenue:
                var links = await ReadArtistVenues(session, ids, cancellationToken);
                var places = await venues.Read(
                    session,
                    links.Select(v => v.venue_id).Distinct().ToArray(),
                    cancellationToken
                );
                foreach (var link in links)
                {
                    link.Venue = places[link.venue_id];
                }
                return new() { ArtistVenues = links };
            case CatalogReferenceKind.Venue:
                return new()
                {
                    Venues = (await venues.Read(session, ids, cancellationToken)).Values.ToArray(),
                };
            case CatalogReferenceKind.Song:
                return new()
                {
                    Songs = await songs.ReadSongDetails(session, ids, cancellationToken),
                };
            case CatalogReferenceKind.SetlistShow:
                return new()
                {
                    SetlistShows = await ReadSetlistShows(session, uuids, cancellationToken),
                };
            default:
                throw new ArgumentOutOfRangeException(nameof(targets));
        }
    }

    /// <summary>Complete ordered recordings share metadata, membership and interpretation snapshot.</summary>
    public async Task<IReadOnlyList<CatalogSource>> ReadRecordings(
        CatalogReadSession session,
        Guid[] uuids,
        CancellationToken cancellationToken
    )
    {
        var sources = await recordings.ReadSources(session, uuids, cancellationToken);
        var imported = await ReadImportedVenues(
            session,
            [],
            sources.Select(s => s.venue_id).OfType<long>().Distinct().ToArray(),
            cancellationToken
        );
        var byId = imported.ToDictionary(v => v.id);
        foreach (var source in sources)
        {
            source.ImportedVenue = source.venue_id is { } id ? byId.GetValueOrDefault(id) : null;
        }
        await ApplyTrackNormalization(
            session,
            sources.SelectMany(s => s.Sets).SelectMany(s => s.Tracks).ToArray(),
            cancellationToken
        );
        return sources;
    }

    internal async Task ApplyTrackNormalization(
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

    internal static async Task<CatalogTrack[]> ReadTracks(
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

    internal static async Task<CatalogSet[]> ReadSets(
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

    internal static async Task<CatalogSourceReview[]> ReadReviews(
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

    internal static async Task<CatalogSource[]> ReadSources(
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

    internal static async Task<CatalogShow[]> ReadShows(
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

    internal static async Task<CatalogSetlistShow[]> ReadSetlistShows(
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

    internal static async Task<CatalogImportedVenue[]> ReadImportedVenues(
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

    internal static async Task<CatalogArtistVenue[]> ReadArtistVenues(
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

    internal static async Task<CatalogYear[]> ReadYears(
        CatalogReadSession session,
        Guid[] uuids,
        CancellationToken cancellationToken
    ) =>
        (
            await session.Connection.QueryAsync<CatalogYear>(
                session.Command(
                    CatalogBrowseReader.YearSelect + " WHERE y.uuid=ANY(@uuids) ORDER BY y.uuid",
                    new { uuids },
                    cancellationToken
                )
            )
        ).ToArray();

    internal async Task<CatalogImportedSong[]> ReadImportedSongs(
        CatalogReadSession session,
        Guid[] uuids,
        CancellationToken cancellationToken
    )
    {
        var imported = (
            await session.Connection.QueryAsync<CatalogImportedSong>(
                session.Command(
                    CatalogSongReader.ImportedSongSelect
                        + " WHERE s.uuid=ANY(@uuids) ORDER BY s.uuid",
                    new { uuids },
                    cancellationToken
                )
            )
        ).ToArray();
        await importedSongMappings.Apply(session, imported, cancellationToken);
        return imported;
    }
}
