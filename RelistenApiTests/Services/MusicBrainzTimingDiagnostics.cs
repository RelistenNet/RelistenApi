using System.Text.Json;
using Dapper;
using Relisten;

namespace RelistenApiTests.Services;

/// <summary>
/// Failure-only timing evidence from immutable acquisition receipts. Deliberately excludes
/// response bodies, headers, URLs and provider payloads; this does not change the clock policy.
/// </summary>
internal static class MusicBrainzTimingDiagnostics
{
    internal static async Task<string> ReadAsync(DbService db, Guid commandUuid)
    {
        var rows = await db.WithWriteConnection(c =>
            c.QueryAsync<TimingRow>(
                """
                SELECT a.attempt_uuid AS "AttemptUuid",a.ordinal AS "Ordinal",a.phase AS "Phase",
                    a.reason AS "AttemptReason",r.eligible AS "Eligible",
                    r.response->>'Reason' AS "ResponseReason",
                    r.response->>'CompletedAt' AS "CompletedAt",
                    a.preparation->>'PreparedAt' AS "PreparedAt",
                    a.preparation->>'SendDeadline' AS "SendDeadline",
                    a.preparation->>'HttpDeadline' AS "HttpDeadline",
                    a.preparation->>'LeaseUntil' AS "LeaseUntil",
                    r.saved_at AS "SavedAt",clock_timestamp() AS "DatabaseNow",
                    gate.attempt_uuid AS "CurrentGateAttemptUuid",gate.lease_until AS "CurrentGateLeaseUntil",
                    CASE WHEN isfinite(gate.not_before) THEN gate.not_before ELSE NULL END AS "CurrentGateNotBefore"
                FROM normalization.musicbrainz_attempts a
                LEFT JOIN normalization.musicbrainz_responses r ON r.attempt_uuid=a.attempt_uuid
                LEFT JOIN normalization.musicbrainz_gate gate ON gate.singleton
                WHERE a.command_uuid=@commandUuid ORDER BY a.ordinal
                """,
                new { commandUuid }
            )
        );
        return JsonSerializer.Serialize(rows);
    }

    private sealed class TimingRow
    {
        public Guid AttemptUuid { get; init; }
        public int Ordinal { get; init; }
        public string Phase { get; init; } = "";
        public string? AttemptReason { get; init; }
        public bool? Eligible { get; init; }
        public string? ResponseReason { get; init; }
        public string? CompletedAt { get; init; }
        public string? PreparedAt { get; init; }
        public string? SendDeadline { get; init; }
        public string? HttpDeadline { get; init; }
        public string? LeaseUntil { get; init; }
        public DateTime? SavedAt { get; init; }
        public DateTime DatabaseNow { get; init; }
        public Guid? CurrentGateAttemptUuid { get; init; }
        public DateTime? CurrentGateLeaseUntil { get; init; }
        public DateTime? CurrentGateNotBefore { get; init; }
    }
}
