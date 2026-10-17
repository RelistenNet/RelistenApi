using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationRecordingLookupStore
{
    private const string HandoffColumns = """
        h.uuid AS "HandoffUuid",h.lookup_attempt_uuid AS "LookupAttemptUuid",
        h.processing_attempt_uuid AS "ProcessingAttemptUuid",h.completed_result::text AS "CompletedResultJson",
        l.mode AS "Mode",a.preparation::text AS "PreparationJson"
        """;
    private const string HandoffJoins = """
        FROM normalization.recording_lookup_handoffs h
        JOIN normalization.recording_lookup_attempts a ON a.attempt_uuid=h.lookup_attempt_uuid
        JOIN normalization.recording_lookups l ON l.uuid=a.lookup_uuid
        """;

    /// <summary>Reads the delivery and original preparation, including a completed acknowledgement.</summary>
    public Task<NormalizationRecordingLookupHandoff?> GetHandoffAsync(
        Guid handoffUuid,
        CancellationToken ct = default
    ) =>
        db.WithWriteConnection<NormalizationRecordingLookupHandoff?>(async c =>
            (await ReadHandoff((NpgsqlConnection)c, null, handoffUuid, false, ct))?.ToHandoff()
        );

    /// <summary>
    /// Bounded stable UUID page of unacknowledged delivery. Filter by mode when dispatching
    /// legacy recordings so retained inputs awaiting a provider consumer cannot starve them.
    /// </summary>
    public Task<IReadOnlyList<NormalizationRecordingLookupHandoff>> GetPendingHandoffsAsync(
        int limit = 100,
        Guid? afterUuid = null,
        NormalizationRecordingLookupMode? mode = null,
        CancellationToken ct = default
    )
    {
        if (
            limit is < 1 or > 1000
            || afterUuid == Guid.Empty
            || mode.HasValue && !Enum.IsDefined(mode.Value)
        )
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                "Use a valid lookup mode, cursor and a limit of 1–1000."
            );
        }
        return db.WithWriteConnection<IReadOnlyList<NormalizationRecordingLookupHandoff>>(async c =>
            (
                await c.QueryAsync<HandoffRow>(
                    new CommandDefinition(
                        $"""
                        SELECT {HandoffColumns} {HandoffJoins}
                        WHERE h.completed_result IS NULL AND (@afterUuid::uuid IS NULL OR h.uuid>@afterUuid)
                            AND (@mode::text IS NULL OR l.mode=@mode)
                        ORDER BY h.uuid LIMIT @limit
                        """,
                        new
                        {
                            limit,
                            afterUuid,
                            mode = mode?.ToString(),
                        },
                        cancellationToken: ct
                    )
                )
            ).Select(row => row.ToHandoff()).ToArray()
        );
    }

    /// <summary>
    /// Acknowledges the saved downstream terminal result. Exact retries ignore its transport
    /// Replayed flag; changed terminal facts conflict. Delivery cannot be acknowledged from
    /// a made-up completion, another preparation, or an unfinished processing attempt.
    /// </summary>
    public Task<NormalizationRecordingLookupHandoff> AcknowledgeHandoffAsync(
        Guid handoffUuid,
        NormalizationRecordingProcessingResult result,
        CancellationToken ct = default
    )
    {
        var stableResult = result with { Replayed = false };
        return InTransaction(
            async (c, t) =>
            {
                var row =
                    await ReadHandoff(c, t, handoffUuid, true, ct)
                    ?? throw new ArgumentException("Unknown lookup handoff.", nameof(handoffUuid));
                var handoff = row.ToHandoff();
                if (
                    stableResult.AttemptUuid != handoff.ProcessingAttemptUuid
                    || stableResult.SourceUuid != handoff.SourceUuid
                )
                {
                    throw new NormalizationConflictException(
                        "Acknowledgement does not match the handoff's processing attempt and source."
                    );
                }
                if (handoff.CompletedResult != null)
                {
                    if (handoff.CompletedResult != stableResult)
                    {
                        throw new NormalizationConflictException(
                            "A different terminal result already acknowledged this handoff."
                        );
                    }
                    return handoff;
                }
                await RequireProcessingCompletion(c, t, handoff, stableResult, ct);
                row.CompletedResultJson = JsonSerializer.Serialize(stableResult);
                await c.ExecuteAsync(
                    new CommandDefinition(
                        """
                        UPDATE normalization.recording_lookup_handoffs SET completed_result=@CompletedResultJson::jsonb,
                            completed_at=clock_timestamp() WHERE uuid=@HandoffUuid
                        """,
                        row,
                        t,
                        cancellationToken: ct
                    )
                );
                return row.ToHandoff();
            },
            ct
        );
    }

    private static async Task RequireProcessingCompletion(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        NormalizationRecordingLookupHandoff handoff,
        NormalizationRecordingProcessingResult result,
        CancellationToken ct
    )
    {
        if (handoff.Mode != NormalizationRecordingLookupMode.LegacyCatalog)
        {
            throw new NormalizationConflictException(
                "Retained provider delivery has no admitted processing consumer yet."
            );
        }
        var processing = await c.QuerySingleOrDefaultAsync<ProcessingCompletionRow>(
            new CommandDefinition(
                """
                SELECT source_uuid AS "SourceUuid",processing_version AS "ProcessingVersion",
                    disposition AS "Disposition",reason AS "Reason",preparation::text AS "PreparationJson"
                FROM normalization.recording_processing_attempts WHERE attempt_uuid=@ProcessingAttemptUuid
                """,
                handoff,
                t,
                cancellationToken: ct
            )
        );
        if (
            processing == null
            || processing.Disposition == null
            || processing.SourceUuid != handoff.SourceUuid
            || processing.ProcessingVersion != handoff.ProcessingVersion
            || !SameJson(
                processing.PreparationJson,
                JsonSerializer.Serialize(handoff.LegacyPreparation)
            )
            || Enum.Parse<NormalizationRecordingProcessingDisposition>(processing.Disposition, true)
                != result.Disposition
            || processing.Reason != result.Reason
        )
        {
            throw new NormalizationConflictException(
                "Acknowledgement does not identify the saved terminal processing result."
            );
        }
    }

    private static Task<HandoffRow?> ReadHandoff(
        NpgsqlConnection c,
        NpgsqlTransaction? t,
        Guid handoffUuid,
        bool forUpdate,
        CancellationToken ct
    ) =>
        c.QuerySingleOrDefaultAsync<HandoffRow>(
            new CommandDefinition(
                $"SELECT {HandoffColumns} {HandoffJoins} WHERE h.uuid=@handoffUuid {(forUpdate ? "FOR UPDATE OF h" : "")}",
                new { handoffUuid },
                t,
                cancellationToken: ct
            )
        );

    private sealed record ProcessingCompletionRow(
        Guid SourceUuid,
        string ProcessingVersion,
        string? Disposition,
        string? Reason,
        string? PreparationJson
    );

    private sealed class HandoffRow
    {
        public Guid HandoffUuid { get; init; }
        public Guid LookupAttemptUuid { get; init; }
        public Guid ProcessingAttemptUuid { get; init; }
        public string Mode { get; init; } = null!;
        public string PreparationJson { get; init; } = null!;
        public string? CompletedResultJson { get; set; }

        public NormalizationRecordingLookupHandoff ToHandoff()
        {
            var preparation = JsonSerializer.Deserialize<NormalizationRecordingLookupPreparation>(
                PreparationJson
            )!;
            return new(
                HandoffUuid,
                LookupAttemptUuid,
                Enum.Parse<NormalizationRecordingLookupMode>(Mode),
                preparation.Binding!.Recording.Value!.SourceUuid,
                ProcessingAttemptUuid,
                preparation.ProcessingVersion,
                preparation.LegacyPreparation,
                CompletedResultJson == null
                    ? null
                    : JsonSerializer.Deserialize<NormalizationRecordingProcessingResult>(
                        CompletedResultJson
                    )
            );
        }
    }
}
