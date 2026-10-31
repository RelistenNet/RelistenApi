using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Relisten.Api.Models;

namespace Relisten.Data
{
    public class SourceTrackPlaysService : RelistenDataServiceBase
    {
        public SourceTrackPlaysService(DbService db)
            : base(db) { }

        private const string InsertPlay = """
            INSERT INTO source_track_plays (source_track_uuid,user_uuid,app_type)
            SELECT @source_track_uuid,@user_uuid,@app_type
            """;

        private const string PlayableTracks = """
            SELECT t.uuid FROM source_tracks t
            JOIN sources src ON src.id=t.source_id
            JOIN source_sets sets ON sets.id=t.source_set_id AND sets.source_id=src.id
            JOIN shows sh ON sh.id=src.show_id JOIN artists a ON a.id=src.artist_id
            WHERE NOT t.is_orphaned
            """;

        public async Task<SourceTrackPlay> RecordPlayedTrack(SourceTrackPlay track)
        {
            return await db.WithWriteConnection(con =>
                con.QuerySingleAsync<SourceTrackPlay>(InsertPlay + " RETURNING *", track)
            );
        }

        /// <summary>
        /// Records v4 telemetry only for a playable catalog track. Validation and
        /// insertion share one statement; a missing/orphaned track writes nothing.
        /// The client UUID is anonymous telemetry, never an account identity.
        /// </summary>
        public async Task<SourceTrackPlay?> RecordPlayableTrack(
            SourceTrackPlay track,
            CancellationToken cancellationToken
        )
        {
            await using var con = db.CreateConnection(longTimeout: false, readOnly: false);
            await con.OpenAsync(cancellationToken);
            // Keep the UUID predicate inside EXISTS so validation uses the track
            // index instead of building a catalog-wide membership set.
            return await con.QuerySingleOrDefaultAsync<SourceTrackPlay>(
                new CommandDefinition(
                    InsertPlay
                        + " WHERE EXISTS ("
                        + PlayableTracks
                        + " AND t.uuid=@source_track_uuid) RETURNING *",
                    track,
                    commandTimeout: 30,
                    cancellationToken: cancellationToken
                )
            );
        }

        /// <summary>
        /// Selects the latest window or the next sequence page, then returns it ascending.
        /// V4 filters public eligibility before LIMIT so removed tracks cannot block polling;
        /// artistUuids optionally restricts that public window to any selected artist.
        /// Legacy callers keep their existing eligibility and response filtering.
        /// </summary>
        public async Task<IEnumerable<SourceTrackPlay>> PlayedTracksSince(
            int? lastSeenId = null,
            int limit = 2000,
            bool publicActivityOnly = false,
            Guid[]? artistUuids = null,
            CancellationToken cancellationToken = default
        )
        {
            await using var con = db.CreateConnection(longTimeout: false, readOnly: true);
            await con.OpenAsync(cancellationToken);
            var eligibility = publicActivityOnly
                ? $"AND EXISTS ({PlayableTracks} AND t.uuid=p.source_track_uuid AND (@artistUuids::uuid[] IS NULL OR a.uuid=ANY(@artistUuids))) AND p.app_type IN (1,2,3,4)"
                : "";
            var tracks = await con.QueryAsync<SourceTrackPlay>(
                new CommandDefinition(
                    $"""
                    SELECT p.* FROM source_track_plays p
                    WHERE p.created_at > now() - interval '1 hour'
                      {(lastSeenId != null ? "AND p.id > @lastSeenId" : "")}
                      {eligibility}
                    ORDER BY p.id {(lastSeenId != null ? "" : "DESC")}
                    LIMIT @limit
                    """,
                    new
                    {
                        lastSeenId,
                        limit,
                        artistUuids,
                    },
                    commandTimeout: 30,
                    cancellationToken: cancellationToken
                )
            );

            return tracks.OrderBy(t => t.id);
        }
    }
}
