using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;

namespace Relisten.Catalog;

/// <summary>
/// Reads recording facts and optional approved interpretations in one primary
/// snapshot. It never invokes ingestion, normalization jobs or legacy services.
/// </summary>
public sealed class CatalogReader(
    DbService db,
    CatalogShowMetadataReader metadata,
    CatalogTrackNormalizationReader trackNormalization,
    CatalogRecordingReader recordings
)
{
    /// <summary>Returns a complete recording hierarchy, or null when the show is unknown.</summary>
    public async Task<CatalogShow?> ReadShow(
        Guid uuid,
        CancellationToken cancellationToken = default
    )
    {
        await using var session = await CatalogReadSession.Open(db, cancellationToken);
        return await ReadShow(session, "s.uuid=@uuid", new { uuid }, cancellationToken);
    }

    /// <summary>Exact imported display-date lookup, scoped to the owning artist.</summary>
    public async Task<CatalogShow?> ReadShowByDate(
        Guid artistUuid,
        string showDate,
        CancellationToken cancellationToken = default,
        bool includeRecordings = true
    )
    {
        await using var session = await CatalogReadSession.Open(db, cancellationToken);
        return await ReadShow(
            session,
            "a.uuid=@artistUuid AND s.display_date=@showDate",
            new { artistUuid, showDate },
            cancellationToken,
            includeRecordings
        );
    }

    /// <summary>Returns one full random show, or null for an unknown/empty artist.</summary>
    public async Task<CatalogShow?> ReadRandomShow(
        Guid artistUuid,
        CancellationToken cancellationToken = default,
        bool includeRecordings = true
    )
    {
        await using var session = await CatalogReadSession.Open(db, cancellationToken);
        return await ReadShow(
            session,
            "s.id=(SELECT s.id FROM shows s JOIN artists a ON a.id=s.artist_id WHERE a.uuid=@artistUuid ORDER BY random() LIMIT 1)",
            new { artistUuid },
            cancellationToken,
            includeRecordings
        );
    }

    private async Task<CatalogShow?> ReadShow(
        CatalogReadSession session,
        string predicate,
        object parameters,
        CancellationToken cancellationToken,
        bool includeRecordings = true
    )
    {
        var show = await session.Connection.QuerySingleOrDefaultAsync<CatalogShow>(
            session.Command(
                CatalogShowMetadataReader.ShowSelect + " WHERE " + predicate,
                parameters,
                cancellationToken
            )
        );
        if (show is null)
        {
            return null;
        }

        if (includeRecordings)
        {
            show.Sources = (
                await recordings.ReadForShow(session, show.id, cancellationToken)
            ).ToList();
        }
        await metadata.Apply(session, [show], cancellationToken);
        await trackNormalization.Apply(
            session,
            show.Sources.SelectMany(x => x.Sets).SelectMany(x => x.Tracks).ToArray(),
            show.normalization_enabled,
            cancellationToken
        );
        return show;
    }

    /// <summary>Returns one complete recording without loading the show's other sources.</summary>
    public async Task<CatalogSource?> ReadSource(
        Guid uuid,
        CancellationToken cancellationToken = default
    )
    {
        await using var session = await CatalogReadSession.Open(db, cancellationToken);
        var source = await recordings.ReadSource(session, uuid, cancellationToken);
        if (source is null)
        {
            return null;
        }

        if (source.venue_id is { } venueId)
        {
            source.ImportedVenue =
                await session.Connection.QuerySingleOrDefaultAsync<CatalogImportedVenue>(
                    session.Command(
                        CatalogImportedVenueMetadataReader.Select + " WHERE v.id=@venueId",
                        new { venueId },
                        cancellationToken
                    )
                );
        }

        var approved = await NormalizationApproved(session, source.artist_uuid, cancellationToken);
        await trackNormalization.Apply(
            session,
            source.Sets.SelectMany(set => set.Tracks).ToArray(),
            approved,
            cancellationToken
        );
        return source;
    }

    /// <summary>Returns one non-orphaned playable file with exact recording parents.</summary>
    public async Task<CatalogTrack?> ReadTrack(
        Guid uuid,
        CancellationToken cancellationToken = default
    )
    {
        await using var session = await CatalogReadSession.Open(db, cancellationToken);
        var track = await session.Connection.QuerySingleOrDefaultAsync<CatalogTrack>(
            session.Command(
                CatalogTrackNormalizationReader.TrackSelect
                    + " WHERE t.uuid=@uuid AND NOT t.is_orphaned",
                new { uuid },
                cancellationToken
            )
        );
        if (track is null)
        {
            return null;
        }

        var approved = await NormalizationApproved(session, track.artist_uuid, cancellationToken);
        await trackNormalization.Apply(session, [track], approved, cancellationToken);
        return track;
    }

    private static Task<bool> NormalizationApproved(
        CatalogReadSession session,
        Guid artistUuid,
        CancellationToken cancellationToken
    ) =>
        session.Connection.QuerySingleAsync<bool>(
            session.Command(
                "SELECT COALESCE(f.norm_catalogue,false) FROM artists a LEFT JOIN features f ON f.artist_id=a.id WHERE a.uuid=@artistUuid",
                new { artistUuid },
                cancellationToken
            )
        );
}
