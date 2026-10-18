using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace Relisten.Data;

public sealed partial class SourceActorAliasApplicationStore
{
    internal const string PublishedStageCertificateVersion = "source-actor-stage-certificate-v1";

    /// <summary>
    /// Reads historical accepted authority, never a pending preparation or today's matching
    /// facts. Certificates are derived only after full initial admission/domain validation,
    /// then retained atomically with the original receipt and Work publication. Current
    /// source/review/target/name/support validation remains the consuming writer's job.
    /// Uses the caller's transaction and takes no additional Work/resource locks.
    /// </summary>
    internal static async Task<
        IReadOnlyDictionary<Guid, SourceActorAliasPersonStageHeader>
    > RequirePublishedStageHeaders(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid applicationUuid,
        IReadOnlyCollection<Guid> stageUuids,
        CancellationToken ct
    )
    {
        var ids = stageUuids.Distinct().ToArray();
        if (
            ids.Any(uuid => uuid == Guid.Empty)
            || ids.Length > SourceActorResolutionStore.MaximumClaims
        )
        {
            throw new ArgumentException(
                "Historical source stage references require a bounded set of original stage UUIDs."
            );
        }
        if (ids.Length == 0)
        {
            return new Dictionary<Guid, SourceActorAliasPersonStageHeader>();
        }

        // Bound actual JSONB transport before transferring any retained prose. A complete
        // application already uses this same budget; the reader is also safe on its own.
        var bytes = await c.QuerySingleAsync<long>(
            new CommandDefinition(
                """
                SELECT coalesce(sum(octet_length(stage_header::text)+coalesce(octet_length(receipt_header::text),0)),0)
                FROM normalization.source_actor_alias_person_stages
                WHERE application_uuid=@applicationUuid AND stage_uuid=ANY(@ids)
                """,
                new { applicationUuid, ids },
                t,
                cancellationToken: ct
            )
        );
        if (bytes > SourceActorAliasApplicationCheckpointCodec.MaximumCheckpointBytes)
        {
            throw new SourceActorReviewCapacityException(
                "source_alias_application_header_capacity_exceeded"
            );
        }
        var rows = (
            await c.QueryAsync<PublishedStageHeaderRow>(
                new CommandDefinition(
                    """
                    SELECT stage.stage_uuid AS "StageUuid",stage.work_uuid AS "WorkUuid",stage.person_uuid AS "PersonUuid",
                        stage.owner_subject_uuid AS "OwnerUuid",stage.predecessor_stage_uuid AS "PredecessorUuid",
                        stage.stage_header::text AS "Header",stage.receipt_header::text AS "Receipt",
                        (stage.receipt IS NOT NULL) AS "HasReceipt",stage.terminal_reason AS "TerminalReason",
                        app.version AS "ApplicationVersion",run.request_hash AS "RequestHash",run.state AS "WorkState",
                        (stage.admitted_work_uuid=stage.work_uuid AND run.uuid=stage.work_uuid
                            AND run.subject_uuid=stage.owner_subject_uuid AND owner.domain='people'
                            AND owner.subject_kind='person' AND owner.subject_uuid=stage.person_uuid) AS "OwnerMatches",
                        (stage.ticket->>'Format'=@format AND stage.ticket->>'Kind'='ticket'
                            AND stage.ticket->'Value'=run.ticket
                            AND run.ticket->>'WorkUuid'=stage.work_uuid::text
                            AND run.ticket->>'SubjectUuid'=stage.owner_subject_uuid::text
                            AND run.ticket->>'PolicyVersion'=app.version) AS "TicketMatches"
                    FROM normalization.source_actor_alias_person_stages stage
                    JOIN normalization.source_actor_alias_applications app ON app.attempt_uuid=stage.application_uuid
                    LEFT JOIN normalization.work_runs run ON run.uuid=stage.admitted_work_uuid
                    LEFT JOIN normalization.work_subjects owner ON owner.uuid=run.subject_uuid
                    WHERE stage.application_uuid=@applicationUuid AND stage.stage_uuid=ANY(@ids)
                    ORDER BY stage.stage_uuid
                    """,
                    new
                    {
                        applicationUuid,
                        ids,
                        format = SourceActorAliasApplicationCheckpointCodec.FormatVersion,
                    },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        if (rows.Length != ids.Length)
        {
            throw new NormalizationConflictException(
                "A historical source stage is absent from this exact application."
            );
        }

        // Exact historical application profiles may prove an existing accepted map. Their
        // own published ticket, policy and receipt remain authoritative only for that past
        // publication; this reader grants no current Add or pending-stage authority.
        var result = new Dictionary<Guid, SourceActorAliasPersonStageHeader>();
        foreach (var row in rows)
        {
            if (
                (
                    row.ApplicationVersion != SourceActorAliasApplicationService.Version
                    && !SourceActorAliasApplicationService.SupportsHistoricalAcceptedApplication(
                        row.ApplicationVersion
                    )
                )
                || !row.HasReceipt
                || row.Receipt == null
                || row.TerminalReason != null
                || row.WorkState != "published"
                || row.OwnerMatches != true
                || row.TicketMatches != true
            )
            {
                throw new NormalizationConflictException(
                    "Historical source authority requires its exact published Work, owner, version and retained ticket."
                );
            }
            var header = DecodeHeader(
                new(
                    row.StageUuid,
                    row.WorkUuid,
                    row.PersonUuid,
                    row.OwnerUuid,
                    row.PredecessorUuid,
                    true,
                    row.Header,
                    row.Receipt,
                    null
                )
            );
            var certificate = header.Receipt!;
            using var receiptMetadata = JsonDocument.Parse(row.Receipt);
            var hasVersion = receiptMetadata.RootElement.TryGetProperty(
                nameof(SourceActorAliasStageReceiptHeader.CertificateVersion),
                out _
            );
            var hasRequest = receiptMetadata.RootElement.TryGetProperty(
                nameof(SourceActorAliasStageReceiptHeader.AuthenticatedRequestHash),
                out _
            );
            if (!hasVersion && !hasRequest)
            {
                // Old committed receipts are not rewritten or guessed into the new protocol.
                // Authenticate their genuine full original proof, then use its exact header.
                var original = await RequireStage(c, t, applicationUuid, row.StageUuid, ct);
                if (original.Receipt == null)
                {
                    throw new NormalizationConflictException(
                        "A legacy source stage lacks its genuine committed receipt."
                    );
                }
                using var expectedMetadata = JsonDocument.Parse(WriteStageHeader(original));
                using var actualMetadata = JsonDocument.Parse(row.Header);
                if (
                    !JsonElement.DeepEquals(
                        actualMetadata.RootElement,
                        expectedMetadata.RootElement
                    )
                    || !JsonElement.DeepEquals(
                        receiptMetadata.RootElement,
                        JsonSerializer.SerializeToElement(
                            new SourceActorAliasStageReceiptHeader(
                                original.Receipt.Names,
                                original.Receipt.RetiredAssignmentUuids.Count,
                                original.Receipt.RetiredSupportUuids.Count,
                                original.Receipt.Disposition,
                                original.Receipt.Reason
                            )
                        )
                    )
                )
                {
                    throw new NormalizationConflictException(
                        "A legacy source stage header differs from its genuine committed proof."
                    );
                }
            }
            else if (
                !hasVersion
                || !hasRequest
                || certificate.CertificateVersion != PublishedStageCertificateVersion
                || certificate.AuthenticatedRequestHash != row.RequestHash
                || certificate.AuthenticatedRequestHash?.Length != 64
            )
            {
                // A partial/unknown certificate is corrupt metadata, not legacy absence.
                throw new NormalizationConflictException(
                    "The historical source stage certificate contradicts its actual admitted request."
                );
            }
            result.Add(row.StageUuid, header);
        }
        return result;
    }

    internal static async Task<SourceActorAliasPersonStageHeader> RequirePublishedStageHeader(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid applicationUuid,
        Guid stageUuid,
        CancellationToken ct
    )
    {
        var headers = await RequirePublishedStageHeaders(c, t, applicationUuid, [stageUuid], ct);
        return headers[stageUuid];
    }

    private sealed record PublishedStageHeaderRow(
        Guid StageUuid,
        Guid WorkUuid,
        Guid PersonUuid,
        Guid OwnerUuid,
        Guid? PredecessorUuid,
        string Header,
        string? Receipt,
        bool HasReceipt,
        string? TerminalReason,
        string ApplicationVersion,
        string? RequestHash,
        string? WorkState,
        bool? OwnerMatches,
        bool? TicketMatches
    );
}
