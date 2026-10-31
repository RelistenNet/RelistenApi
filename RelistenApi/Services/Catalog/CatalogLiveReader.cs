using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Relisten.Api.Models;
using Relisten.Data;

namespace Relisten.Catalog;

/// <summary>
/// Hydrates the public recent-activity window in batches. Events can outlive
/// recordings, so missing/orphaned tracks are omitted. This is not account history.
/// </summary>
public sealed class CatalogLiveReader(
    DbService db,
    SourceTrackPlaysService plays,
    CatalogShowMetadataReader metadata,
    CatalogTrackNormalizationReader normalization
)
{
    public async Task<IReadOnlyList<CatalogLiveEvent>> ReadHistory(
        int? afterSequence,
        int limit,
        CancellationToken cancellationToken
    )
    {
        var events = (
            await plays.PlayedTracksSince(
                afterSequence,
                limit,
                publicActivityOnly: true,
                cancellationToken: cancellationToken
            )
        ).ToArray();
        if (events.Length == 0)
        {
            return [];
        }

        await using var session = await CatalogReadSession.Open(db, cancellationToken);
        var tracks = (
            await session.Connection.QueryAsync<CatalogTrack>(
                session.Command(
                    CatalogTrackNormalizationReader.TrackSelect
                        + " WHERE t.uuid=ANY(@ids) AND NOT t.is_orphaned",
                    new
                    {
                        ids = events.Select(play => play.source_track_uuid).Distinct().ToArray(),
                    },
                    cancellationToken
                )
            )
        ).ToDictionary(track => track.uuid);
        var shows = (
            await session.Connection.QueryAsync<CatalogShow>(
                session.Command(
                    CatalogShowMetadataReader.ShowSelect + " WHERE s.uuid=ANY(@ids)",
                    new
                    {
                        ids = tracks.Values.Select(track => track.show_uuid).Distinct().ToArray(),
                    },
                    cancellationToken
                )
            )
        ).ToDictionary(show => show.uuid);
        await metadata.Apply(session, shows.Values.ToArray(), cancellationToken);

        // Approval is per artist, but interpretation can be loaded for all approved
        // tracks together. The disabled batch remains usable with imported metadata.
        foreach (
            var batch in tracks.Values.GroupBy(track =>
                shows[track.show_uuid].normalization_enabled
            )
        )
        {
            await normalization.Apply(session, batch.ToArray(), batch.Key, cancellationToken);
        }

        return events
            // The source query filters before its limit. Only a recording removed
            // between that read and this catalog snapshot can now be missing.
            .Where(play => tracks.ContainsKey(play.source_track_uuid))
            .Select(play => new CatalogLiveEvent(
                play,
                tracks[play.source_track_uuid],
                shows[tracks[play.source_track_uuid].show_uuid]
            ))
            .ToArray();
    }
}

public sealed record CatalogLiveEvent(SourceTrackPlay Play, CatalogTrack Track, CatalogShow Show);
