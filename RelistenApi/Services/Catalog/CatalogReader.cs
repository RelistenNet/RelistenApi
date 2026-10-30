using System;
using System.Collections.Generic;
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
    CatalogTrackNormalizationReader trackNormalization
)
{
    /// <summary>Returns a complete recording hierarchy, or null when the show is unknown.</summary>
    public async Task<CatalogShow?> ReadShow(
        Guid uuid,
        CancellationToken cancellationToken = default
    )
    {
        await using var session = await CatalogReadSession.Open(db, cancellationToken);
        var show = await session.Connection.QuerySingleOrDefaultAsync<CatalogShow>(
            session.Command(
                CatalogShowMetadataReader.ShowSelect + " WHERE s.uuid=@uuid",
                new { uuid },
                cancellationToken
            )
        );
        if (show is null)
        {
            return null;
        }

        await ReadRecordings(session, show, cancellationToken);
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
        var source = await session.Connection.QuerySingleOrDefaultAsync<CatalogSource>(
            session.Command(SourceSelect + " WHERE src.uuid=@uuid", new { uuid }, cancellationToken)
        );
        if (source is null)
        {
            return null;
        }

        await ReadRecordingContents(session, [source], cancellationToken);
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

    private static async Task ReadRecordings(
        CatalogReadSession session,
        CatalogShow show,
        CancellationToken cancellationToken
    )
    {
        var sources = (
            await session.Connection.QueryAsync<CatalogSource>(
                session.Command(
                    SourceSelect
                        + " WHERE src.show_id=@id ORDER BY src.avg_rating_weighted DESC,src.uuid",
                    new { show.id },
                    cancellationToken
                )
            )
        ).ToList();
        await ReadRecordingContents(session, sources, cancellationToken);
        show.Sources = sources;
    }

    // Complete sets, tracks and links belong to the requested source IDs. Both full-show
    // and direct-source reads use this loader, so playback and replacement semantics agree.
    private static async Task ReadRecordingContents(
        CatalogReadSession session,
        IReadOnlyList<CatalogSource> sources,
        CancellationToken cancellationToken
    )
    {
        if (sources.Count == 0)
        {
            return;
        }

        var sourceIds = sources.Select(source => source.id).ToArray();
        var sets = (
            await session.Connection.QueryAsync<CatalogSet>(
                session.Command(
                    """
                    SELECT sets.*, src.uuid source_uuid, a.uuid artist_uuid
                    FROM source_sets sets JOIN sources src ON src.id=sets.source_id JOIN artists a ON a.id=src.artist_id
                    WHERE src.id=ANY(@sourceIds) ORDER BY sets.index,sets.uuid
                    """,
                    new { sourceIds },
                    cancellationToken
                )
            )
        ).ToArray();
        var tracks = (
            await session.Connection.QueryAsync<CatalogTrack>(
                session.Command(
                    CatalogTrackNormalizationReader.TrackSelect
                        + " WHERE src.id=ANY(@sourceIds) AND NOT t.is_orphaned ORDER BY t.track_position,t.uuid",
                    new { sourceIds },
                    cancellationToken
                )
            )
        ).ToArray();
        var links = (
            await session.Connection.QueryAsync<CatalogLink>(
                session.Command(
                    """
                    SELECT l.*, src.uuid source_uuid, up.name provider FROM links l JOIN sources src ON src.id=l.source_id
                    JOIN upstream_sources up ON up.id=l.upstream_source_id
                    WHERE src.id=ANY(@sourceIds) ORDER BY l.id
                    """,
                    new { sourceIds },
                    cancellationToken
                )
            )
        ).ToArray();
        var tracksBySet = tracks.ToLookup(x => x.source_set_id);
        var setsBySource = sets.ToLookup(x => x.source_id);
        var linksBySource = links.ToLookup(x => x.source_id);
        foreach (var set in sets)
        {
            set.Tracks = tracksBySet[set.id].ToList();
        }
        foreach (var source in sources)
        {
            source.Sets = setsBySource[source.id].ToList();
            source.Links = linksBySource[source.id].ToList();
        }
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

    private const string SourceSelect = """
        SELECT src.*,a.uuid artist_uuid,sh.uuid show_uuid,
               COALESCE(rc.source_review_count,0) review_count
        FROM sources src JOIN artists a ON a.id=src.artist_id JOIN shows sh ON sh.id=src.show_id
        LEFT JOIN source_review_counts rc ON rc.source_id=src.id
        """;
}
