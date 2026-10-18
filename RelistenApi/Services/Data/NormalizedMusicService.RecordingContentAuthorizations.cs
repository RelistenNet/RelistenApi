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

public sealed partial class NormalizedMusicService
{
    /// <summary>
    /// Historical-exact approval changes correspondence authority only. Original source tickets
    /// remain historical; this command cannot refresh publication clocks or mark metadata reviewed.
    /// </summary>
    public Task<NormalizationReviewResult> ReviewRecordingContentCorrespondenceAsync(
        RecordingContentCorrespondenceReview command,
        string trustedOperator,
        CancellationToken ct = default
    )
    {
        if (
            command.OperationUuid == Guid.Empty
            || command.SubjectUuid == Guid.Empty
            || command.SourceUuid == Guid.Empty
            || command.Approvals.Count is 0 or > 10000
            || string.IsNullOrWhiteSpace(command.Reason)
            || JsonSerializer.SerializeToUtf8Bytes(command).Length
                > NormalizationRecordingContentMaterializer.MaximumImageBytes
            || command.Approvals.Any(item =>
                item.AuthorizationUuid == Guid.Empty
                || item.Targets.Count != 1
                || item.Targets.Any(target =>
                    target.Uuid == Guid.Empty || target.ExpectedRevision <= 0
                )
                || !item.Withdrawal && item.Source == null
                || item.Evidence.Count == 0
            )
        )
        {
            throw new ArgumentException(
                "Recording correspondence review requires bounded exact source, targets and retained evidence."
            );
        }
        command = JsonSerializer.Deserialize<RecordingContentCorrespondenceReview>(
            JsonSerializer.Serialize(command)
        )!;
        return work.ApplyReviewAsync(
            new(
                command.OperationUuid,
                command.SubjectUuid,
                command.ExpectedEditorialRevision,
                "replace",
                JsonSerializer.SerializeToElement(command),
                command.Reason,
                trustedOperator
            ),
            async (c, t, token) =>
            {
                await NormalizedPeopleService.ValidateOwnerAsync(
                    c,
                    t,
                    command.SubjectUuid,
                    "songs",
                    "recording",
                    command.SourceUuid,
                    token
                );
                var heads = await ReadRecordingContentAuthorizationHeadsAsync(
                    c,
                    t,
                    command.SourceUuid,
                    token
                );
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var approval in command.Approvals)
                {
                    ValidateAuthorizationAction(approval.Action);
                    var key = RecordingAuthorizationKey(
                        approval.Source,
                        approval.Withdrawal,
                        approval.Targets[0].Uuid
                    );
                    if (!keys.Add(key))
                    {
                        throw new ArgumentException(
                            "Each authority key may be reviewed only once per command."
                        );
                    }
                    var previous = heads.SingleOrDefault(head =>
                        RecordingAuthorizationKey(
                            head.Source,
                            head.Withdrawal,
                            head.Targets[0].Uuid
                        ) == key
                    );
                    if (previous?.AuthorizationUuid != approval.ExpectedPriorAuthorizationUuid)
                    {
                        throw new NormalizationConflictException(
                            "The correspondence approval, block or revocation changed after review."
                        );
                    }
                    await NormalizedPeopleService.ValidateEvidenceAsync(
                        c,
                        t,
                        approval.Evidence,
                        token
                    );
                    var target = approval.Targets[0];
                    var valid = await c.ExecuteScalarAsync<bool>(
                        new CommandDefinition(
                            """
                            SELECT EXISTS(SELECT 1 FROM normalization.track_contents x JOIN normalization.music_revisions r ON r.uuid=x.revision_uuid
                                WHERE x.uuid=@Uuid AND r.revision=@ExpectedRevision
                                    AND (@track::uuid IS NULL OR x.source_track_uuid=@track)
                                    AND COALESCE((SELECT s.uuid FROM source_tracks tr JOIN sources s ON s.id=tr.source_id
                                        WHERE tr.uuid=x.source_track_uuid),x.source_uuid)=@source)
                            """,
                            new
                            {
                                target.Uuid,
                                target.ExpectedRevision,
                                source = command.SourceUuid,
                                track = approval.Source?.TrackUuid,
                            },
                            t,
                            cancellationToken: token
                        )
                    );
                    if (!valid)
                    {
                        throw new NormalizationConflictException(
                            "The reviewed content target or its current recording scope changed."
                        );
                    }
                    var originalSource = approval.Source;
                    if (originalSource == null && approval.Withdrawal)
                    {
                        var known = await c.QuerySingleOrDefaultAsync<string>(
                            new CommandDefinition(
                                "SELECT source_reference::text FROM normalization.recording_content_correspondences WHERE source_uuid=@SourceUuid AND content_uuid=@Uuid AND NOT withdrawn ORDER BY sequence DESC LIMIT 1",
                                new { command.SourceUuid, target.Uuid },
                                t,
                                cancellationToken: token
                            )
                        );
                        originalSource =
                            known == null
                                ? null
                                : JsonSerializer.Deserialize<RecordingContentSourceReference>(
                                    known
                                );
                        if (originalSource == null)
                        {
                            throw new ArgumentException(
                                "Withdrawal review requires an actual accepted source-to-content correspondence."
                            );
                        }
                    }
                    var tickets =
                        originalSource == null
                            ? []
                            : new[]
                            {
                                await RequireHistoricalRecordingClaimAsync(
                                    c,
                                    t,
                                    command.SourceUuid,
                                    originalSource,
                                    token
                                ),
                            };
                    var record = new RecordingContentAuthorizationRecord(
                        approval.AuthorizationUuid,
                        command.SourceUuid,
                        approval.Source,
                        approval.Targets,
                        approval.Action,
                        approval.Withdrawal,
                        approval.Evidence,
                        OperationUuid: command.OperationUuid
                    );
                    await WriteRecordingAuthorizationAsync(
                        c,
                        t,
                        record,
                        approval.ExpectedPriorAuthorizationUuid,
                        tickets,
                        token
                    );
                }
            },
            ct
        );
    }

    public Task<
        IReadOnlyList<RecordingContentAuthorizationRecord>
    > GetRecordingContentAuthorizationsAsync(
        Guid sourceUuid,
        int limit = 100,
        Guid? afterUuid = null,
        CancellationToken ct = default
    )
    {
        ValidateLimit(limit);
        return db.WithWriteConnection<IReadOnlyList<RecordingContentAuthorizationRecord>>(
            async connection =>
            {
                var c = (NpgsqlConnection)connection;
                await using var t = await c.BeginTransactionAsync(
                    System.Data.IsolationLevel.RepeatableRead,
                    ct
                );
                var bound = await c.ExecuteScalarAsync<long>(
                    new CommandDefinition(
                        """
                        SELECT COALESCE(sum(bytes),0)::bigint FROM (SELECT octet_length(payload::text) AS bytes
                            FROM normalization.recording_content_authorization_events WHERE source_uuid=@sourceUuid
                            AND (@afterUuid::uuid IS NULL OR uuid>@afterUuid) ORDER BY uuid LIMIT @limit) page
                        """,
                        new
                        {
                            sourceUuid,
                            afterUuid,
                            limit,
                        },
                        t,
                        cancellationToken: ct
                    )
                );
                if (bound > 2L * NormalizationRecordingContentMaterializer.MaximumImageBytes)
                {
                    throw new ArgumentException(
                        "The recording authorization history page exceeds 32 MiB."
                    );
                }
                var rows = await c.QueryAsync<RecordingAuthorityRow>(
                    new CommandDefinition(
                        """
                        SELECT e.payload::text AS "Payload",EXISTS(SELECT 1 FROM normalization.recording_content_authorization_heads h
                            WHERE h.authorization_uuid=e.uuid) AS "Active" FROM normalization.recording_content_authorization_events e
                        WHERE e.source_uuid=@sourceUuid AND (@afterUuid::uuid IS NULL OR e.uuid>@afterUuid) ORDER BY e.uuid LIMIT @limit
                        """,
                        new
                        {
                            sourceUuid,
                            afterUuid,
                            limit,
                        },
                        t,
                        cancellationToken: ct
                    )
                );
                return rows.Select(row =>
                        JsonSerializer.Deserialize<RecordingContentAuthorizationRecord>(
                            row.Payload
                        )! with
                        {
                            Active = row.Active,
                        }
                    )
                    .ToArray();
            }
        );
    }

    internal static async Task<
        IReadOnlyList<RecordingContentAuthorizationRecord>
    > ReadRecordingContentAuthorizationHeadsAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid source,
        CancellationToken ct,
        NormalizationSubjectObservation? capacityOwner = null
    )
    {
        var bound = await c.QuerySingleAsync<RecordingAuthorityBound>(
            new CommandDefinition(
                """
                SELECT count(*) AS "Count",COALESCE(sum(octet_length(e.payload::text)),0)::bigint AS "Bytes"
                FROM normalization.recording_content_authorization_heads h JOIN normalization.recording_content_authorization_events e ON e.uuid=h.authorization_uuid
                WHERE h.source_uuid=@source
                """,
                new { source },
                t,
                cancellationToken: ct
            )
        );
        if (
            bound.Count > 10000
            || bound.Bytes > NormalizationRecordingContentMaterializer.MaximumImageBytes
        )
        {
            throw new MusicMaterializationCapacityException(
                "The recording authorization heads exceed the complete-image capacity profile.",
                capacityOwner
            );
        }
        var rows = await c.QueryAsync<string>(
            new CommandDefinition(
                "SELECT e.payload::text FROM normalization.recording_content_authorization_heads h JOIN normalization.recording_content_authorization_events e ON e.uuid=h.authorization_uuid WHERE h.source_uuid=@source ORDER BY h.authorization_key",
                new { source },
                t,
                cancellationToken: ct
            )
        );
        return rows.Select(json =>
                JsonSerializer.Deserialize<RecordingContentAuthorizationRecord>(json)!
            )
            .ToArray();
    }

    private static async Task WriteRecordingAuthorizationAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        RecordingContentAuthorizationRecord record,
        Guid? previous,
        IReadOnlyList<NormalizationWorkTicket> tickets,
        CancellationToken ct
    )
    {
        var key = RecordingAuthorizationKey(
            record.Source,
            record.Withdrawal,
            record.Targets[0].Uuid
        );
        await c.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO normalization.recording_content_authorization_events(uuid,source_uuid,operation_uuid,processor_decision_uuid,
                    authorization_key,kind,action,prior_uuid,payload,source_tickets)
                VALUES(@AuthorizationUuid,@SourceUuid,@OperationUuid,@ProcessorDecisionUuid,@key,@kind,@action,@previous,@payload::jsonb,@tickets::jsonb);
                INSERT INTO normalization.recording_content_authorization_heads(source_uuid,authorization_key,authorization_uuid)
                VALUES(@SourceUuid,@key,@AuthorizationUuid) ON CONFLICT(source_uuid,authorization_key)
                DO UPDATE SET authorization_uuid=EXCLUDED.authorization_uuid
                """,
                new
                {
                    record.AuthorizationUuid,
                    record.SourceUuid,
                    record.OperationUuid,
                    record.ProcessorDecisionUuid,
                    key,
                    kind = record.Withdrawal ? "removal" : "correspondence",
                    action = record.Action.ToString().ToLowerInvariant(),
                    previous,
                    payload = JsonSerializer.Serialize(record),
                    tickets = JsonSerializer.Serialize(tickets),
                },
                t,
                cancellationToken: ct
            )
        );
    }

    private static async Task<NormalizationWorkTicket> RequireHistoricalRecordingClaimAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid source,
        RecordingContentSourceReference reference,
        CancellationToken ct
    )
    {
        var bytes = await c.ExecuteScalarAsync<long?>(
            new CommandDefinition(
                "SELECT octet_length(batch::text) FROM normalization.recording_processing_attempts WHERE attempt_uuid=@uuid AND source_uuid=@source AND disposition='published'",
                new { uuid = reference.ExtractionAttemptUuid, source },
                t,
                cancellationToken: ct
            )
        );
        if (bytes == null || bytes > NormalizationRepertoireResolutionStore.MaximumCheckpointBytes)
        {
            throw new ArgumentException(
                "The historical recording source batch is unavailable or exceeds its bound."
            );
        }
        var json = await c.QuerySingleAsync<string>(
            new CommandDefinition(
                "SELECT batch::text FROM normalization.recording_processing_attempts WHERE attempt_uuid=@uuid",
                new { uuid = reference.ExtractionAttemptUuid },
                t,
                cancellationToken: ct
            )
        );
        var batch = JsonSerializer.Deserialize<NormalizationRecordingProposalBatch>(json)!;
        var valid = await c.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                "SELECT EXISTS(SELECT 1 FROM normalization.work_runs WHERE uuid=@WorkUuid AND state='published' AND ticket=@ticket::jsonb)",
                new { batch.Ticket.WorkUuid, ticket = JsonSerializer.Serialize(batch.Ticket) },
                t,
                cancellationToken: ct
            )
        );
        var input = NormalizationRecordingReadSet
            .Inputs(batch.Preparation)
            .SingleOrDefault(input =>
                input.Binding.InputRevisionUuid == reference.InputRevisionUuid
            );
        var extraction =
            batch.Preparation.Binding.InputRevisionUuid == reference.InputRevisionUuid
                ? batch.Extraction
                : batch
                    .AdditionalExtractions?.SingleOrDefault(input =>
                        input.InputRevisionUuid == reference.InputRevisionUuid
                    )
                    ?.Extraction;
        var proposal = extraction?.Titles.SingleOrDefault(proposal =>
            proposal.ProposalUuid == reference.ProposalUuid
            && proposal.TrackUuid == reference.TrackUuid
        );
        var file = input?.Binding.Files.SingleOrDefault(file =>
            file.Binding.Value?.TrackUuid == reference.TrackUuid
        );
        if (
            !valid
            || batch.SourceUuid != source
            || batch.ProcessingVersion != NormalizationRecordingProcessor.Version
            || !NormalizationRecordingReadSet.IsComplete(batch.Ticket, batch.Preparation)
            || proposal?.Extraction?.Segments.Any(segment =>
                segment.Position == reference.SegmentPosition
            ) != true
            || file == null
            || !NormalizationRepertoireResolutionStore.Same(
                file.Observation.Origin,
                reference.FileOrigin
            )
        )
        {
            throw new ArgumentException(
                "The reviewed source must identify an exact compatible published file/title segment."
            );
        }
        var claimValid = await c.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                """
                SELECT EXISTS(SELECT 1 FROM normalization.repertoire_resolution_attempts a,
                    jsonb_array_elements(a.preparation->'Source'->'Claims') claim
                    WHERE a.request->'Source'->>'PublishedProcessingAttemptUuid'=@extraction
                        AND (claim->>'ClaimUuid')::uuid=@ClaimUuid AND claim->>'CorrespondenceKey'=@CorrespondenceKey
                        AND (claim->>'ProposalUuid')::uuid=@ProposalUuid AND (claim->>'TrackUuid')::uuid=@TrackUuid
                        AND (claim->'Origin'->'Evidence'->>'InputRevisionUuid')::uuid=@InputRevisionUuid)
                """,
                new
                {
                    extraction = reference.ExtractionAttemptUuid.ToString("D"),
                    reference.ClaimUuid,
                    reference.CorrespondenceKey,
                    reference.ProposalUuid,
                    reference.TrackUuid,
                    reference.InputRevisionUuid,
                },
                t,
                cancellationToken: ct
            )
        );
        if (!claimValid)
        {
            throw new ArgumentException(
                "The reviewed claim UUID must retain its actual original repertoire source coordinate."
            );
        }
        return batch.Ticket;
    }

    internal static string RecordingAuthorizationKey(
        RecordingContentSourceReference? source,
        bool withdrawal,
        Guid target
    ) =>
        withdrawal
            ? "remove:" + target.ToString("D")
            : "claim:"
                + source!.InputRevisionUuid.ToString("D")
                + ":"
                + source.ProposalUuid.ToString("D")
                + ":"
                + source.TrackUuid.ToString("D")
                + ":"
                + source.SegmentPosition;

    private sealed record RecordingAuthorityRow(string Payload, bool Active);

    private sealed record RecordingAuthorityBound(long Count, long Bytes);
}
