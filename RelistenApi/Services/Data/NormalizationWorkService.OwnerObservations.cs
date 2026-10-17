using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationWorkService
{
    private static Guid[]? NormalizeObservationKeys(IEnumerable<Guid>? keys)
    {
        var normalized = (keys ?? []).Distinct().Order().ToArray();
        if (normalized.Any(uuid => uuid == Guid.Empty))
        {
            throw new ArgumentException("Observed owners require stable subject UUIDs.");
        }
        return normalized.Length == 0 ? null : normalized;
    }

    private static NormalizationSubjectObservation[] NormalizeOwnerObservations(
        IReadOnlyList<NormalizationSubjectObservation>? observations
    )
    {
        var normalized = (observations ?? [])
            .Distinct()
            .OrderBy(input => input.SubjectUuid)
            .ToArray();
        NormalizeObservationKeys(normalized.Select(input => input.SubjectUuid));
        if (
            normalized.Any(input =>
                input.DesiredGeneration < 0
                || input.EditorialRevision < 0
                || input.PublishedWorkUuid == Guid.Empty
            ) || normalized.GroupBy(input => input.SubjectUuid).Any(group => group.Count() != 1)
        )
        {
            throw new ArgumentException(
                "Owner observations require one consistent nonnegative original stamp per subject."
            );
        }
        return normalized;
    }

    private static void RequireOwnerObservations(
        NormalizationSubjectObservation[] expected,
        WorkBindingSet bindings,
        IReadOnlyList<SubjectRow> subjects
    )
    {
        if (
            !expected
                .Select(input => input.SubjectUuid)
                .SequenceEqual(bindings.ObservedSubjects ?? [])
        )
        {
            throw new NormalizationConflictException(
                "Supply original owner observations for the complete binding union."
            );
        }
        // Pools fence new rivals and relationship membership, including credit owners absent
        // from this list. The typed facts projection must select every scope it actually reads.
        if (expected.Length > 0 && (bindings.CandidateUniverses?.Length ?? 0) == 0)
        {
            throw new ArgumentException(
                "Owner observations require consumed candidate universes from the same facts snapshot."
            );
        }
        if (!expected.All(input => subjects.Any(subject => MatchesObservation(input, subject))))
        {
            throw new NormalizationConflictException(
                "Observed owners changed after their facts were read; reload the complete snapshot."
            );
        }
    }

    private static bool MatchesObservation(
        NormalizationSubjectObservation input,
        SubjectRow subject
    ) =>
        input.SubjectUuid == subject.Uuid
        && input.DesiredGeneration == subject.DesiredGeneration
        && input.EditorialRevision == subject.EditorialRevision
        && input.PublishedWorkUuid == subject.PublishedWorkUuid;

    private static bool OwnerObservationsAreCurrent(
        NormalizationWorkTicket ticket,
        IReadOnlyList<SubjectRow> subjects
    ) =>
        (ticket.OwnerObservations ?? []).All(input =>
            // Admission may configure the observing owner itself. Its original stamp stays in
            // the audit ticket; IsCurrent checks the configured primary ticket state instead.
            input.SubjectUuid == ticket.SubjectUuid
            || subjects.Any(subject => MatchesObservation(input, subject))
        );

    private static Task ConfigureOwnerObservations(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid subjectUuid,
        IReadOnlyList<Guid> observedSubjects,
        CancellationToken ct
    ) =>
        connection.ExecuteAsync(
            new CommandDefinition(
                """
                DELETE FROM normalization.work_owner_observations WHERE subject_uuid=@subjectUuid AND NOT (observed_subject_uuid=ANY(@observedSubjects));
                INSERT INTO normalization.work_owner_observations(subject_uuid,observed_subject_uuid)
                    SELECT @subjectUuid,unnest(@observedSubjects::uuid[]) ON CONFLICT DO NOTHING
                """,
                new { subjectUuid, observedSubjects = observedSubjects.ToArray() },
                transaction,
                cancellationToken: ct
            )
        );

    private static Task RecordOwnerObservationPublication(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid subjectUuid,
        CancellationToken ct
    ) =>
        connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE normalization.work_owner_observations i SET published_desired_generation=s.desired_generation,
                    published_editorial_revision=s.editorial_revision
                FROM normalization.work_subjects s WHERE i.subject_uuid=@subjectUuid AND s.uuid=i.observed_subject_uuid
                """,
                new { subjectUuid },
                transaction,
                cancellationToken: ct
            )
        );
}
