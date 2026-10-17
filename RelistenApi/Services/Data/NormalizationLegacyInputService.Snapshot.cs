using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

// Both representations use one bounded projection. V1 keeps its historical complete rows and
// observation timestamp. V2 removes operational fields so equal normalization facts reuse bytes.
public sealed partial class NormalizationLegacyInputService
{
    private Task<SnapshotRow?> ReadSnapshotAsync(
        Guid sourceUuid,
        bool stableProjection,
        CancellationToken ct
    ) =>
        db.WithWriteConnection(async connection =>
        {
            await using var transaction = await (
                (NpgsqlConnection)connection
            ).BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
            await connection.ExecuteAsync(
                new CommandDefinition(
                    "SET TRANSACTION READ ONLY",
                    transaction: transaction,
                    cancellationToken: ct
                )
            );
            var snapshot = await connection.QuerySingleOrDefaultAsync<SnapshotRow>(
                new CommandDefinition(
                    SnapshotSql,
                    new
                    {
                        sourceUuid,
                        stableProjection,
                        maxTracks = MaximumTracks,
                        maxSets = MaximumSets,
                        maxBytes = MaximumPayloadBytes,
                    },
                    transaction,
                    cancellationToken: ct
                )
            );
            if (snapshot != null && stableProjection)
            {
                RequireSnapshotBounds(snapshot);
                snapshot.CatalogInputs = await ReadSnapshotClocksAsync(
                    (NpgsqlConnection)connection,
                    transaction,
                    snapshot,
                    ct
                );
            }

            await transaction.CommitAsync(ct);
            return snapshot;
        });

    private static void RequireSnapshotBounds(SnapshotRow snapshot)
    {
        if (
            snapshot.TrackCount > MaximumTracks
            || snapshot.SetCount > MaximumSets
            || snapshot.ByteCount > MaximumPayloadBytes
        )
        {
            throw new InvalidDataException(
                "Legacy recording snapshot exceeds its track, set or byte limit."
            );
        }
    }

    private static async Task<
        IReadOnlyList<NormalizationCatalogDependency>
    > ReadSnapshotClocksAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SnapshotRow snapshot,
        CancellationToken ct
    )
    {
        // Cross-linked rows remain evidence. Their owners must join the read set because their
        // changes need not bump the requested recording artist's clock. Missing derived references
        // remain unknown; the catalog binder decides whether a file or show is usable.
        if (!snapshot.HasRecordingArtist)
        {
            throw new NormalizationConflictException(
                "Catalog snapshot has no recording artist; repair it before refreshing."
            );
        }

        var clocks = (
            await connection.QueryAsync<ContextClockRow>(
                new CommandDefinition(
                    """
                    SELECT a.uuid AS "ArtistUuid",r.generation AS "Generation"
                    FROM artists a LEFT JOIN normalization.catalog_artist_revisions r ON r.artist_uuid=a.uuid
                    WHERE a.id=ANY(@ids) ORDER BY a.uuid
                    """,
                    new { ids = snapshot.ContextArtistIds },
                    transaction,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        if (
            clocks.Length != snapshot.ContextArtistIds.Length
            || clocks.Any(clock => clock.Generation == null)
        )
        {
            throw new NormalizationConflictException(
                "Catalog snapshot has missing artist revisions; repair them before refreshing."
            );
        }

        return clocks
            .Select(clock => new NormalizationCatalogDependency(
                clock.ArtistUuid,
                clock.Generation!.Value
            ))
            .ToArray();
    }

    private sealed record ContextClockRow(Guid ArtistUuid, long? Generation);

    private sealed class SnapshotRow
    {
        public string? Payload { get; init; }
        public int TrackCount { get; init; }
        public int SetCount { get; init; }
        public long ByteCount { get; init; }
        public DateTime SnapshotAt { get; init; }
        public int[] ContextArtistIds { get; init; } = [];
        public bool HasRecordingArtist { get; init; }
        public IReadOnlyList<NormalizationCatalogDependency> CatalogInputs { get; set; } = [];
    }

    private const string SnapshotSql = """
        WITH projection_policy AS (
            SELECT CASE WHEN @stableProjection THEN ARRAY['created_at','updated_at'] ELSE '{}'::text[] END AS row_ignored,
                CASE WHEN @stableProjection THEN ARRAY['created_at','updated_at','avg_rating','avg_rating_weighted','num_reviews','num_ratings'] ELSE '{}'::text[] END AS source_ignored,
                CASE WHEN @stableProjection THEN ARRAY['created_at','updated_at','avg_rating'] ELSE '{}'::text[] END AS show_ignored,
                CASE WHEN @stableProjection THEN ARRAY['snapshot_at'] ELSE '{}'::text[] END AS envelope_ignored
        ), recording AS (
            SELECT s.* FROM sources s WHERE s.uuid=@sourceUuid
        ), selected_sets AS MATERIALIZED (
            SELECT ss.* FROM source_sets ss JOIN recording s ON s.id=ss.source_id
            ORDER BY ss.index,ss.id LIMIT (@maxSets + 1)
        ), source_track_ids AS MATERIALIZED (
            SELECT t.id FROM source_tracks t JOIN recording s ON s.id=t.source_id
            LIMIT (@maxTracks + 1)
        ), set_track_ids AS MATERIALIZED (
            SELECT t.id FROM source_tracks t JOIN source_sets ss ON ss.id=t.source_set_id
            JOIN recording s ON s.id=ss.source_id LIMIT (@maxTracks + 1)
        ), selected_tracks AS MATERIALIZED (
            SELECT t.* FROM source_tracks t JOIN (
                SELECT id FROM source_track_ids UNION SELECT id FROM set_track_ids
            ) related ON related.id=t.id
            ORDER BY t.id LIMIT (@maxTracks + 1)
        ), related_track_sources AS (
            SELECT ts.* FROM sources ts WHERE ts.id IN (
                SELECT source_id FROM selected_tracks
                UNION SELECT ss.source_id FROM source_sets ss JOIN selected_tracks t ON t.source_set_id=ss.id
            )
        ), context_artists AS (
            SELECT artist_id FROM recording
            UNION SELECT sh.artist_id FROM shows sh JOIN recording s ON s.show_id=sh.id
            UNION SELECT v.artist_id FROM venues v JOIN recording s ON s.venue_id=v.id
            UNION SELECT v.artist_id FROM venues v JOIN shows sh ON sh.venue_id=v.id JOIN recording s ON s.show_id=sh.id
            UNION SELECT artist_id FROM selected_tracks
            UNION SELECT artist_id FROM related_track_sources
        ), snapshot AS (
            SELECT (jsonb_build_object(
                'representation','legacy_catalog',
                'snapshot_at',transaction_timestamp(),
                'source',to_jsonb(s)-p.source_ignored,
                'artist',CASE WHEN a.id IS NULL THEN NULL ELSE jsonb_build_object(
                    'id',a.id,'uuid',a.uuid,'name',a.name,'slug',a.slug,'musicbrainz_id',a.musicbrainz_id) END,
                'show',to_jsonb(sh)-p.show_ignored,
                'source_venue',to_jsonb(sv)-p.row_ignored,
                'show_venue',to_jsonb(shv)-p.row_ignored,
                'configured_artist_mappings',COALESCE((
                    SELECT jsonb_agg(jsonb_build_object(
                        'upstream_source_id',m.upstream_source_id,'upstream_provider',u.name,
                        'upstream_identifier',m.upstream_identifier) ORDER BY m.upstream_source_id,m.upstream_identifier)
                    FROM artists_upstream_sources m LEFT JOIN upstream_sources u ON u.id=m.upstream_source_id
                    WHERE m.artist_id=s.artist_id
                ),'[]'::jsonb),
                'sets',COALESCE((SELECT jsonb_agg(to_jsonb(ss)-p.row_ignored ORDER BY ss.index,ss.id) FROM selected_sets ss),'[]'::jsonb),
                'tracks',COALESCE((SELECT jsonb_agg(jsonb_build_object(
                    'track',to_jsonb(t)-p.row_ignored,
                    'source_context',CASE WHEN ts.id IS NULL THEN NULL ELSE jsonb_build_object(
                        'id',ts.id,'uuid',ts.uuid,'artist_id',ts.artist_id,'show_id',ts.show_id) END,
                    'set_context',to_jsonb(ss)-p.row_ignored,
                    'artist_context',CASE WHEN ta.id IS NULL THEN NULL ELSE jsonb_build_object(
                        'id',ta.id,'uuid',ta.uuid,'name',ta.name,'slug',ta.slug) END
                ) ORDER BY ss.index NULLS LAST,t.track_position,t.id)
                    FROM selected_tracks t
                    LEFT JOIN source_sets ss ON ss.id=t.source_set_id
                    LEFT JOIN sources ts ON ts.id=t.source_id
                    LEFT JOIN artists ta ON ta.id=t.artist_id
                ),'[]'::jsonb)
            )-p.envelope_ignored)::text AS payload,
            (SELECT count(*)::int FROM selected_tracks) AS track_count,
            (SELECT count(*)::int FROM selected_sets) AS set_count,
            transaction_timestamp() AS snapshot_at,
            ARRAY(SELECT owner.id FROM artists owner JOIN context_artists context ON context.artist_id=owner.id
                ORDER BY owner.id) AS context_artist_ids,
            (a.id IS NOT NULL) AS has_recording_artist
            FROM recording s
            CROSS JOIN projection_policy p
            LEFT JOIN artists a ON a.id=s.artist_id
            LEFT JOIN shows sh ON sh.id=s.show_id
            LEFT JOIN venues sv ON sv.id=s.venue_id
            LEFT JOIN venues shv ON shv.id=sh.venue_id
        )
        SELECT CASE WHEN octet_length(payload)<=@maxBytes THEN payload END AS "Payload",
            track_count AS "TrackCount",set_count AS "SetCount",octet_length(payload)::bigint AS "ByteCount",
            snapshot_at AS "SnapshotAt",context_artist_ids AS "ContextArtistIds",has_recording_artist AS "HasRecordingArtist"
        FROM snapshot
        """;
}
