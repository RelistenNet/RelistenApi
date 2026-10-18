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
    /// <summary>
    /// Select configuration by exact admitted header, never timestamp, union generation alone,
    /// or accepted row inventory. Running stages are returned for exact recovery. A stale stage
    /// retains its committed predecessor map but never supplies its prospective map as authority.
    /// Must run on the same primary repeatable-read snapshot as membership and accepted rows.
    /// </summary>
    internal static async Task<SourceActorAliasPersonStage?> GetConfiguredStageInSnapshotAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid originalPersonUuid,
        NormalizationInputContributionsSnapshot membership,
        CancellationToken ct
    )
    {
        if (membership.Owner == null)
        {
            return null;
        }
        var owner = membership.Owner;
        if (owner.Subject != new NormalizationSubjectKey("people", "person", originalPersonUuid))
        {
            throw new ArgumentException(
                "Configured-map membership belongs to another original person."
            );
        }
        var current = membership
            .Contributions.Select(value => new NormalizationInputContributionRevision(
                value.Contributor,
                value.Revision
            ))
            .Concat(
                membership.UnmanagedBaseline is { } baseline ? [new(baseline.Contributor, 0)] : []
            )
            .ToArray();
        RequireUniqueHeaders(current);
        var sourceHeader = membership.Contributions.SingleOrDefault(value =>
            value.Contributor == StableContributor
        );
        if (sourceHeader == null)
        {
            return null;
        }
        var rows = await c.QueryAsync<ConfiguredRow>(
            new CommandDefinition(
                """
                SELECT stage.stage::text AS "Stage",stage.ticket::text AS "Ticket",stage.receipt::text AS "Receipt",
                    stage.terminal_reason AS "TerminalReason",run.ticket::text AS "WorkTicket",run.state AS "State",app.version AS "Version",run.request_hash AS "RequestHash"
                FROM normalization.source_actor_alias_person_stages stage
                JOIN normalization.source_actor_alias_applications app ON app.attempt_uuid=stage.application_uuid
                JOIN normalization.work_runs run ON run.uuid=stage.work_uuid
                JOIN normalization.work_subjects owner ON owner.uuid=run.subject_uuid
                WHERE stage.person_uuid=@originalPersonUuid AND stage.owner_subject_uuid=@ownerUuid
                    AND stage.contribution_revision=@revision
                    AND owner.domain='people' AND owner.subject_kind='person' AND owner.subject_uuid=@originalPersonUuid
                """,
                new
                {
                    originalPersonUuid,
                    ownerUuid = owner.SubjectUuid,
                    revision = sourceHeader.Revision,
                },
                t,
                cancellationToken: ct
            )
        );
        SourceActorAliasPersonStage? selected = null;
        foreach (var row in rows)
        {
            var stage = DecodeStage(
                new StageRow(row.Stage, row.Ticket, row.Receipt, row.TerminalReason)
            );
            var actualTicket =
                JsonSerializer.Deserialize<NormalizationWorkTicket>(row.WorkTicket)
                ?? throw new NormalizationConflictException(
                    "Configured source alias Work has no retained ticket."
                );
            if (
                stage.OwnerSubjectUuid != owner.SubjectUuid
                || stage.OriginalPersonUuid != originalPersonUuid
                || actualTicket.SubjectUuid != owner.SubjectUuid
                || actualTicket.WorkUuid != stage.WorkUuid
                || actualTicket.PolicyVersion != row.Version
                || !NormalizationWorkService.MatchesContributedBatchRequest(
                    row.RequestHash,
                    new("people", "person", stage.OriginalPersonUuid),
                    row.Version,
                    stage.Contributions,
                    stage.ExpectedConfigurationGeneration,
                    stage.ExpectedContributions
                )
                || (
                    stage.Ticket != null
                    && !SourceActorAliasApplicationCheckpointCodec.Same(stage.Ticket, actualTicket)
                )
            )
            {
                throw new NormalizationConflictException(
                    "Configured source alias stage differs from its actual admitted Work header."
                );
            }
            // Admission can commit before the append-once domain attachment. Expose that
            // actual historical ticket for discovery; the recovering application must attach
            // it before publication. This does not modify or restamp the stage preparation.
            stage = stage with
            {
                Ticket = actualTicket,
            };
            if (!SameHeaders(AdmittedHeaders(stage), current))
            {
                continue;
            }
            RequireConfiguredBindings(stage, membership.Contributions);
            if (row.State == "published" && stage.Receipt == null)
            {
                throw new InvalidOperationException(
                    "Published source alias Work is missing its atomic domain map receipt."
                );
            }
            if (row.State != "published" && stage.Receipt != null)
            {
                throw new InvalidOperationException(
                    "Unpublished source alias Work cannot have an accepted map receipt."
                );
            }
            if (selected != null)
            {
                throw new NormalizationConflictException(
                    "More than one source alias stage claims the exact configured header and revision vector."
                );
            }
            selected = stage;
        }
        return selected;
    }

    /// <summary>
    /// Called inside Work after replay/freshness/lock checks, never before entering Work.
    /// This method does not acquire another Work owner or update the preparation's observations.
    /// </summary>
    internal static async Task<SourceActorAliasPersonStage> RequireStage(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid applicationUuid,
        Guid stageUuid,
        CancellationToken ct
    )
    {
        var row = await c.QuerySingleOrDefaultAsync<Guid?>(
            new CommandDefinition(
                "SELECT application_uuid FROM normalization.source_actor_alias_person_stages WHERE stage_uuid=@stageUuid",
                new { stageUuid },
                t,
                cancellationToken: ct
            )
        );
        if (row != applicationUuid)
        {
            throw new NormalizationConflictException(
                "Publication stage does not belong to the requested application."
            );
        }
        var stage =
            await ReadStage(c, t, stageUuid, ct)
            ?? throw new ArgumentException("Unknown source alias stage.");
        if (stage.Ticket == null || stage.TerminalReason != null)
        {
            throw new NormalizationConflictException(
                "Source alias stage lacks its exact live admitted Work ticket."
            );
        }
        var work = await c.QuerySingleOrDefaultAsync<WorkRow>(
            new CommandDefinition(
                """
                SELECT run.state AS "State",run.ticket::text AS "Ticket",run.request_hash AS "RequestHash",owner.domain AS "Domain",
                    owner.subject_kind AS "Kind",owner.subject_uuid AS "PersonUuid"
                FROM normalization.work_runs run JOIN normalization.work_subjects owner ON owner.uuid=run.subject_uuid
                WHERE run.uuid=@WorkUuid AND run.subject_uuid=@OwnerSubjectUuid
                """,
                new { stage.WorkUuid, stage.OwnerSubjectUuid },
                t,
                cancellationToken: ct
            )
        );
        if (
            work == null
            || work.Domain != "people"
            || work.Kind != "person"
            || work.PersonUuid != stage.OriginalPersonUuid
            || work.State == "stale"
            || !NormalizationWorkService.MatchesContributedBatchRequest(
                work.RequestHash,
                new("people", "person", stage.OriginalPersonUuid),
                stage.Ticket.PolicyVersion,
                stage.Contributions,
                stage.ExpectedConfigurationGeneration,
                stage.ExpectedContributions
            )
            || !SourceActorAliasApplicationCheckpointCodec.Same(
                stage.Ticket,
                JsonSerializer.Deserialize<NormalizationWorkTicket>(work.Ticket)
            )
        )
        {
            throw new NormalizationConflictException(
                "Source alias publication is not bound to its exact original-person Work header."
            );
        }
        if (
            work.State == "published" && stage.Receipt == null
            || work.State != "published" && stage.Receipt != null
        )
        {
            throw new InvalidOperationException(
                "Source alias Work state differs from its atomic domain receipt."
            );
        }
        return stage;
    }

    internal async Task WriteStagePublishedAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid applicationUuid,
        SourceActorAliasStageReceipt receipt,
        CancellationToken ct
    )
    {
        var stage = await RequireStage(c, t, applicationUuid, receipt.StageUuid, ct);
        if (
            receipt.WorkUuid != stage.WorkUuid
            || receipt.OriginalPersonUuid != stage.OriginalPersonUuid
        )
        {
            throw new ArgumentException(
                "Receipt must retain the exact stage Work and original person."
            );
        }
        if (stage.Receipt != null)
        {
            if (!SourceActorAliasApplicationCheckpointCodec.Same(stage.Receipt, receipt))
            {
                throw new NormalizationConflictException(
                    "Stage already has another immutable publication receipt."
                );
            }
            return;
        }
        // The last byte guard is inside the accepted write transaction. Overflow rolls back
        // aliases, support retirement, the map and the Work watermark together.
        var json = SourceActorAliasApplicationCheckpointCodec.WriteReceipt(receipt);
        // RequireStage has just authenticated the complete original contributed request.
        // Its verified hash certifies that historical header reads need not reconstruct the
        // old source/candidate proof. The full immutable stage and receipt remain the audit.
        var requestHash = await c.QuerySingleAsync<string>(
            new CommandDefinition(
                "SELECT request_hash FROM normalization.work_runs WHERE uuid=@WorkUuid",
                new { stage.WorkUuid },
                t,
                cancellationToken: ct
            )
        );
        var expectedStageHeader = WriteStageHeader(stage);
        var header = System.Text.Json.JsonSerializer.Serialize(
            new SourceActorAliasStageReceiptHeader(
                receipt.Names,
                receipt.RetiredAssignmentUuids.Count,
                receipt.RetiredSupportUuids.Count,
                receipt.Disposition,
                receipt.Reason,
                PublishedStageCertificateVersion,
                requestHash
            )
        );
        await RequireApplicationHeaderCapacity(c, t, applicationUuid, null, header, ct);
        var updated = await c.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE normalization.source_actor_alias_person_stages SET receipt=@json::jsonb,receipt_header=@header::jsonb
                WHERE application_uuid=@applicationUuid AND stage_uuid=@StageUuid AND receipt IS NULL AND terminal_reason IS NULL
                    AND stage_header=@expectedStageHeader::jsonb
                    AND work_uuid=@WorkUuid AND person_uuid=@OriginalPersonUuid AND owner_subject_uuid=@OwnerSubjectUuid
                """,
                new
                {
                    applicationUuid,
                    receipt.StageUuid,
                    stage.WorkUuid,
                    stage.OriginalPersonUuid,
                    stage.OwnerSubjectUuid,
                    json,
                    header,
                    expectedStageHeader,
                },
                t,
                cancellationToken: ct
            )
        );
        if (updated != 1)
        {
            throw new NormalizationConflictException(
                "Source alias receipt lost its unfinished stage transition."
            );
        }
    }

    private static void RequireConfiguredBindings(
        SourceActorAliasPersonStage stage,
        IReadOnlyList<NormalizationInputContributionState> current
    ) =>
        RequireConfiguredAllocations(
            stage
                .Contributions.Select(SourceActorAliasContributionAllocation.FromPrepared)
                .ToArray(),
            current
        );

    private static IReadOnlyList<NormalizationInputContributionRevision> AdmittedHeaders(
        SourceActorAliasPersonStage stage
    ) =>
        AdmittedHeaders(
            stage.ExpectedContributions,
            stage.Contributions.Select(value => value.Contribution).ToArray(),
            stage.Facts.Membership.UnmanagedBaseline?.Contributor
        );

    private static IReadOnlyList<NormalizationInputContributionRevision> AdmittedHeaders(
        IReadOnlyList<NormalizationInputContributionRevision> priorHeaders,
        IReadOnlyList<NormalizationInputContribution> replacements,
        NormalizationResourceKey? unmanagedBaseline
    )
    {
        RequireUniqueHeaders(priorHeaders);
        if (
            replacements.Select(value => value.Contributor).Distinct().Count() != replacements.Count
        )
        {
            throw new NormalizationConflictException(
                "Saved stage repeats a contributor replacement."
            );
        }
        var expected = priorHeaders.ToDictionary(
            value => value.Contributor,
            value => value.Revision
        );
        foreach (var replacement in replacements)
        {
            // Work adopts a previously unmanaged baseline at revision 1 before applying an
            // explicitly prepared baseline replacement. Its original observed CAS remains 0.
            var prior = expected.GetValueOrDefault(replacement.Contributor);
            var required = unmanagedBaseline == replacement.Contributor ? 1 : prior;
            if (replacement.ExpectedRevision != required)
            {
                throw new NormalizationConflictException(
                    "Replacement revision differs from the saved complete prior header."
                );
            }
            expected[replacement.Contributor] = checked(replacement.ExpectedRevision + 1);
        }
        if (
            unmanagedBaseline is { } adopted
            && !replacements.Any(value => value.Contributor == adopted)
        )
        {
            expected[adopted] = 1;
        }
        return expected
            .Select(value => new NormalizationInputContributionRevision(value.Key, value.Value))
            .ToArray();
    }

    private static void RequireUniqueHeaders(
        IReadOnlyList<NormalizationInputContributionRevision> values
    )
    {
        if (
            values.Select(value => value.Contributor).Distinct().Count() != values.Count
            || values.Any(value => value.Revision < 0)
        )
        {
            throw new ArgumentException(
                "Contribution revision headers must be unique, complete and nonnegative."
            );
        }
    }

    private static bool SameHeaders(
        IReadOnlyList<NormalizationInputContributionRevision> left,
        IReadOnlyList<NormalizationInputContributionRevision> right
    )
    {
        RequireUniqueHeaders(left);
        RequireUniqueHeaders(right);
        var expected = left.ToDictionary(value => value.Contributor, value => value.Revision);
        return expected.Count == right.Count
            && right.All(value =>
                expected.TryGetValue(value.Contributor, out var revision)
                && revision == value.Revision
            );
    }

    private sealed record ConfiguredRow(
        string Stage,
        string? Ticket,
        string? Receipt,
        string? TerminalReason,
        string WorkTicket,
        string State,
        string Version,
        string RequestHash
    );

    private sealed record WorkRow(
        string State,
        string Ticket,
        string RequestHash,
        string Domain,
        string Kind,
        Guid PersonUuid
    );
}
