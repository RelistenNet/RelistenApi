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

public sealed partial class NormalizationSetlistMaterializationStore
{
    internal Task<SetlistMaterializationAttempt?> ReadForPublicationAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid attemptUuid,
        CancellationToken ct
    ) => Read(c, t, attemptUuid, false, true, ct);

    internal async Task<SetlistMaterializationAttempt> RequireForPublicationAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid attemptUuid,
        Guid workUuid,
        CancellationToken ct
    )
    {
        var attempt = await RequireAttempt(c, t, attemptUuid, ct);
        if (
            attempt.Result != null
            || attempt.Preparation == null
            || attempt.Preparation.ContextOmitted
            || attempt.Ticket == null
            || attempt.Receipt != null
            || attempt.AdmissionFailureReason != null
            || workUuid != attempt.AttemptUuid
        )
        {
            throw new NormalizationConflictException(
                "Publication requires its unfinished, fully prepared consumer admission."
            );
        }
        await RequireActualRun(c, t, attempt, attempt.Ticket, ct, true);
        RequireTicket(attempt, attempt.Ticket);
        return attempt;
    }

    internal static void RequireTicket(
        SetlistMaterializationAttempt attempt,
        NormalizationWorkTicket ticket
    )
    {
        var context = attempt.Preparation!.Context!;
        var read = context.ReadSet;
        var savedOwner = context.OwnerContext.Membership.Owner;
        if (
            ticket.WorkUuid != attempt.AttemptUuid
            || ticket.PolicyVersion != attempt.ProcessingVersion
            || ticket.EditorialRevision != (savedOwner?.EditorialRevision ?? 0)
            || savedOwner != null && ticket.SubjectUuid != savedOwner.SubjectUuid
            || !SetEquals(ticket.Inputs, read.Inputs)
            || !SetEquals(ticket.Dependencies, read.Dependencies)
            || !SetEquals(ticket.CatalogInputs ?? [], read.CatalogInputs)
            || !SetEquals(ticket.SourceIdentities ?? [], read.SourceIdentities)
            || ticket.MappingRegistry != read.MappingRegistry
            || !SetEquals(ticket.ImportedVenues ?? [], read.ImportedVenues)
            || !SetEquals(ticket.CandidateUniverses ?? [], read.CandidateUniverses)
            || !SetEquals(ticket.OwnerObservations ?? [], read.OwnerObservations)
            || !SetEquals(ticket.SourceUuids ?? [], read.SourceUuids)
        )
        {
            throw new NormalizationConflictException(
                "The consumer must preserve every original preparation stamp."
            );
        }
    }

    private static async Task RequireActualRun(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SetlistMaterializationAttempt attempt,
        NormalizationWorkTicket ticket,
        CancellationToken ct,
        bool publishing = false
    )
    {
        var row = await c.QuerySingleOrDefaultAsync<WorkRow>(
            new CommandDefinition(
                "SELECT s.domain AS \"Domain\",s.subject_kind AS \"Kind\",s.subject_uuid AS \"Uuid\",r.state AS \"State\",r.ticket::text AS \"Ticket\" FROM normalization.work_runs r JOIN normalization.work_subjects s ON s.uuid=r.subject_uuid WHERE r.uuid=@uuid",
                new { uuid = ticket.WorkUuid },
                t,
                cancellationToken: ct
            )
        );
        var owner = attempt.Preparation!.Context!.Owner;
        if (
            ticket.WorkUuid != attempt.AttemptUuid
            || row == null
            || (publishing ? row.State != "running" : row.State is not ("running" or "stale"))
            || owner != new NormalizationSubjectKey(row.Domain, row.Kind, row.Uuid)
            || !Same(ticket, JsonSerializer.Deserialize<NormalizationWorkTicket>(row.Ticket))
        )
        {
            throw new NormalizationConflictException(
                "Consumer admission requires its exact actual Work ticket and owner."
            );
        }
    }

    internal async Task WritePublishedAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid attemptUuid,
        NormalizationPublicationResult receipt,
        CancellationToken ct,
        MusicMaterializationResult? result = null
    )
    {
        var json = Serialize(receipt, MaximumDecisionPageBytes);
        var current = await RequireAttempt(c, t, attemptUuid, ct);
        if (current.Receipt != null)
        {
            RequireSame(
                current.Receipt,
                receipt,
                "The original consumer publication receipt is immutable."
            );
            return;
        }
        if (
            current.Result != null
            || current.Ticket == null
            || current.AdmissionFailureReason != null
            || receipt.WorkUuid != attemptUuid
            || receipt.Status != NormalizationPublicationStatus.Published
        )
        {
            throw new NormalizationConflictException(
                "Only a successful admitted consumer publication records a receipt."
            );
        }
        await c.ExecuteAsync(
            new CommandDefinition(
                "UPDATE normalization.setlist_materialization_attempts SET receipt=@json::jsonb,checkpoint_revision=checkpoint_revision+1 WHERE attempt_uuid=@attemptUuid",
                new { attemptUuid, json },
                t,
                cancellationToken: ct
            )
        );
        current = current with
        {
            Receipt = receipt,
            CheckpointRevision = current.CheckpointRevision + 1,
        };

        var adopted = await c.ExecuteAsync(
            new CommandDefinition(
                "UPDATE normalization.setlist_materialization_registrations SET edition_uuid=@edition WHERE registration_uuid=@uuid AND current_attempt_uuid IS NOT DISTINCT FROM @previous::uuid",
                new
                {
                    edition = current.Preparation!.Context!.Owner.Kind == "setlist"
                        ? (Guid?)current.Preparation.Context!.EditionUuid
                        : null,
                    uuid = current.Registration.RegistrationUuid,
                    previous = current.Registration.CurrentAttemptUuid,
                },
                t,
                cancellationToken: ct
            )
        );
        if (adopted != 1)
        {
            throw new NormalizationConflictException(
                "A newer completed consumer attempt already owns this registration."
            );
        }
        if (current.Preparation.RequiresObservationRetirement && !RetirementsCompleted(current))
        {
            return;
        }

        var completed = result ?? BuildCompletedResult(current);
        RequireCompletedImage(current);
        await SaveResult(c, t, current, completed, ct);
    }

    private static MusicMaterializationResult BuildCompletedResult(
        SetlistMaterializationAttempt attempt
    )
    {
        var decisions = attempt.Preparation!.Decisions;
        return new(
            attempt.AttemptUuid,
            MusicMaterializationDisposition.Completed,
            "materialization_acknowledged",
            decisions.Count(value =>
                value.Disposition
                    is MusicMaterializationDecisionDisposition.Accepted
                        or MusicMaterializationDecisionDisposition.Withdrawn
            ),
            decisions.Count(value =>
                value.Disposition == MusicMaterializationDecisionDisposition.Retained
            ),
            decisions.Count(value =>
                value.Disposition == MusicMaterializationDecisionDisposition.Review
            ),
            decisions.Count(value =>
                value.Disposition == MusicMaterializationDecisionDisposition.Unresolved
            )
        );
    }

    private static void RequireCompletedImage(SetlistMaterializationAttempt attempt)
    {
        if (
            attempt.Receipt?.Status != NormalizationPublicationStatus.Published
            || attempt.AdmissionFailureReason != null
        )
        {
            throw new NormalizationConflictException(
                "A complete materialization requires its real accepted or decision-only publication."
            );
        }
        if (attempt.Preparation!.RequiresObservationRetirement && !RetirementsCompleted(attempt))
        {
            throw new NormalizationConflictException(
                "Complete the obsolete source watcher retirement first."
            );
        }
    }

    private static async Task SaveResult(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SetlistMaterializationAttempt attempt,
        MusicMaterializationResult result,
        CancellationToken ct
    )
    {
        if (
            result.AttemptUuid != attempt.AttemptUuid
            || result.Disposition == MusicMaterializationDisposition.Pending
        )
        {
            throw new ArgumentException("A terminal result must identify this consumer attempt.");
        }
        if (result.Disposition == MusicMaterializationDisposition.Completed)
        {
            RequireCompletedImage(attempt);
            var changed = await c.ExecuteAsync(
                new CommandDefinition(
                    "UPDATE normalization.setlist_materialization_registrations SET current_attempt_uuid=@uuid,edition_uuid=@edition,observation_retired=@retired WHERE registration_uuid=@registrationUuid AND (current_attempt_uuid IS NOT DISTINCT FROM @previous::uuid OR current_attempt_uuid=@uuid)",
                    new
                    {
                        uuid = attempt.AttemptUuid,
                        registrationUuid = attempt.Registration.RegistrationUuid,
                        previous = attempt.Registration.CurrentAttemptUuid,
                        edition = attempt.Preparation!.Context!.Owner.Kind == "setlist"
                            ? (Guid?)attempt.Preparation.Context!.EditionUuid
                            : null,
                        retired = attempt.Preparation.Context!.Owner.Kind
                        == "setlist_materialization"
                            ? false
                            : attempt.Registration.ObservationRetired
                                || attempt.CompletedRetirements.Any(value =>
                                    value.Owner.Kind == "setlist_materialization"
                                ),
                    },
                    t,
                    cancellationToken: ct
                )
            );
            if (changed != 1)
            {
                throw new NormalizationConflictException(
                    "A newer completed consumer attempt already owns this registration."
                );
            }
        }
        await c.ExecuteAsync(
            new CommandDefinition(
                "UPDATE normalization.setlist_materialization_attempts SET result=@json::jsonb,checkpoint_revision=checkpoint_revision+1 WHERE attempt_uuid=@uuid",
                new
                {
                    uuid = attempt.AttemptUuid,
                    json = Serialize(result, MaximumDecisionPageBytes),
                },
                t,
                cancellationToken: ct
            )
        );
    }

    private static bool SetEquals<T>(IEnumerable<T> left, IEnumerable<T> right) =>
        left.ToHashSet().SetEquals(right);

    private sealed record WorkRow(
        string Domain,
        string Kind,
        Guid Uuid,
        string State,
        string Ticket
    );
}
