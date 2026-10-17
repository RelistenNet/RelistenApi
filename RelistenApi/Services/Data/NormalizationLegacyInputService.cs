using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

/// <summary>
/// Seeds missing normalization evidence from the stored catalog without provider HTTP. The snapshot
/// retains catalog identities and incomplete relationships; it does not establish provider provenance
/// or playability. Existing evidence is never refreshed by this service.
/// </summary>
public sealed class NormalizationLegacyInputService(
    DbService db,
    NormalizationEvidenceService evidence
)
{
    public const string CaptureVersion = "legacy-recording-snapshot-v1";
    private const int MaximumTracks = 10_000;
    private const int MaximumSets = 1_000;
    private const int MaximumPayloadBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Retains one bounded, consistent recording snapshot only when its resource has no current input.
    /// Missing sources return Missing. Oversized snapshots fail without truncation or capture.
    /// Call outside catalog transactions; evidence must survive a later catalog rollback.
    /// </summary>
    public async Task<NormalizationLegacySeedResult> SeedRecordingAsync(
        Guid sourceUuid,
        CancellationToken ct = default
    )
    {
        if (sourceUuid == Guid.Empty)
        {
            throw new ArgumentException("A catalog source UUID is required.", nameof(sourceUuid));
        }
        if (Transaction.Current != null)
        {
            throw new InvalidOperationException(
                "Seed legacy evidence outside the catalog transaction."
            );
        }

        ct.ThrowIfCancellationRequested();
        var resource = new NormalizationResourceKey(
            "legacy_catalog",
            "recording",
            sourceUuid.ToString("D")
        );
        var current = await evidence.GetCurrentInputAsync(resource, ct);
        if (current != null)
        {
            return new(NormalizationLegacySeedDisposition.Existing, current);
        }

        var snapshot = await ReadSnapshotAsync(sourceUuid, ct);
        if (snapshot == null)
        {
            return new(NormalizationLegacySeedDisposition.Missing, null);
        }
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

        // No catalog transaction remains open. Zero-only admission prevents this historical fallback
        // from replacing any seed or newer evidence that arrived while the snapshot was read.
        var retained = await evidence.CaptureInputAsync(
            new(
                Guid.NewGuid(),
                resource,
                Encoding.UTF8.GetBytes(snapshot.Payload!),
                "application/json; charset=utf-8",
                CaptureVersion,
                0,
                new DateTimeOffset(snapshot.SnapshotAt)
            ),
            ct
        );
        current =
            await evidence.GetCurrentInputAsync(resource, ct)
            ?? throw new InvalidOperationException(
                "Retained legacy evidence has no current input."
            );
        return new(
            retained.Fetch.AcceptedAsCurrent
                ? NormalizationLegacySeedDisposition.Seeded
                : NormalizationLegacySeedDisposition.Existing,
            current
        );
    }

    private Task<SnapshotRow?> ReadSnapshotAsync(Guid sourceUuid, CancellationToken ct) =>
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
                        maxTracks = MaximumTracks,
                        maxSets = MaximumSets,
                        maxBytes = MaximumPayloadBytes,
                    },
                    transaction,
                    cancellationToken: ct
                )
            );
            await transaction.CommitAsync(ct);
            return snapshot;
        });

    private sealed record SnapshotRow(
        string? Payload,
        int TrackCount,
        int SetCount,
        long ByteCount,
        DateTime SnapshotAt
    );

    private const string SnapshotSql = """
        WITH recording AS (
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
        ), snapshot AS (
            SELECT jsonb_build_object(
                'representation','legacy_catalog',
                'snapshot_at',transaction_timestamp(),
                'source',to_jsonb(s),
                'artist',CASE WHEN a.id IS NULL THEN NULL ELSE jsonb_build_object(
                    'id',a.id,'uuid',a.uuid,'name',a.name,'slug',a.slug,'musicbrainz_id',a.musicbrainz_id) END,
                'show',to_jsonb(sh),
                'source_venue',to_jsonb(sv),
                'show_venue',to_jsonb(shv),
                'configured_artist_mappings',COALESCE((
                    SELECT jsonb_agg(jsonb_build_object(
                        'upstream_source_id',m.upstream_source_id,'upstream_provider',u.name,
                        'upstream_identifier',m.upstream_identifier) ORDER BY m.upstream_source_id,m.upstream_identifier)
                    FROM artists_upstream_sources m LEFT JOIN upstream_sources u ON u.id=m.upstream_source_id
                    WHERE m.artist_id=s.artist_id
                ),'[]'::jsonb),
                'sets',COALESCE((SELECT jsonb_agg(to_jsonb(ss) ORDER BY ss.index,ss.id) FROM selected_sets ss),'[]'::jsonb),
                'tracks',COALESCE((SELECT jsonb_agg(jsonb_build_object(
                    'track',to_jsonb(t),
                    'source_context',CASE WHEN ts.id IS NULL THEN NULL ELSE jsonb_build_object(
                        'id',ts.id,'uuid',ts.uuid,'artist_id',ts.artist_id,'show_id',ts.show_id) END,
                    'set_context',to_jsonb(ss),
                    'artist_context',CASE WHEN ta.id IS NULL THEN NULL ELSE jsonb_build_object(
                        'id',ta.id,'uuid',ta.uuid,'name',ta.name,'slug',ta.slug) END
                ) ORDER BY ss.index NULLS LAST,t.track_position,t.id)
                    FROM selected_tracks t
                    LEFT JOIN source_sets ss ON ss.id=t.source_set_id
                    LEFT JOIN sources ts ON ts.id=t.source_id
                    LEFT JOIN artists ta ON ta.id=t.artist_id
                ),'[]'::jsonb)
            )::text AS payload,
            (SELECT count(*)::int FROM selected_tracks) AS track_count,
            (SELECT count(*)::int FROM selected_sets) AS set_count,
            transaction_timestamp() AS snapshot_at
            FROM recording s
            LEFT JOIN artists a ON a.id=s.artist_id
            LEFT JOIN shows sh ON sh.id=s.show_id
            LEFT JOIN venues sv ON sv.id=s.venue_id
            LEFT JOIN venues shv ON shv.id=sh.venue_id
        )
        SELECT CASE WHEN octet_length(payload)<=@maxBytes THEN payload END AS "Payload",
            track_count AS "TrackCount",set_count AS "SetCount",octet_length(payload)::bigint AS "ByteCount",
            snapshot_at AS "SnapshotAt" FROM snapshot
        """;
}
