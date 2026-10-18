using System;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationRecordingContentMaterializationStore
{
    private static async Task RequireCurrentRetryParent(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid registrationUuid,
        Guid previousUuid,
        string version,
        CancellationToken ct
    )
    {
        // The lineage advisory lock covers every direct/retry header insertion. Checking the
        // actual latest leaf here prevents a delayed caller from branching behind a newer failure.
        var latest = await c.QuerySingleOrDefaultAsync<Guid?>(
            new CommandDefinition(
                """
                SELECT attempt.attempt_uuid FROM normalization.recording_content_materialization_attempts attempt
                WHERE attempt.registration_uuid=@registrationUuid
                    AND NOT EXISTS(SELECT 1 FROM normalization.recording_content_materialization_attempts child
                        WHERE child.previous_attempt_uuid=attempt.attempt_uuid)
                ORDER BY attempt.sequence DESC LIMIT 1 FOR UPDATE OF attempt
                """,
                new { registrationUuid },
                t,
                cancellationToken: ct
            )
        );
        if (latest != previousUuid)
        {
            throw new NormalizationConflictException(
                "The requested retry parent is no longer this registration's current leaf."
            );
        }
        var previous = (await Read(c, t, previousUuid, false, false, ct))!;
        var requirement = previous.Result?.RetryRequirement;
        if (
            requirement == null
            || requirement.ProcessorVersion != version
            || requirement.LimitProfile != NormalizationRecordingContentMaterializer.LimitProfile
        )
        {
            return;
        }

        var sourceChanged = await c.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                """
                SELECT EXISTS(SELECT 1 FROM normalization.repertoire_resolution_attempts selected
                    JOIN normalization.repertoire_resolution_registrations account ON account.registration_uuid=selected.registration_uuid
                    JOIN normalization.repertoire_resolution_attempts newer ON newer.attempt_uuid=account.current_attempt_uuid
                    WHERE selected.attempt_uuid=@selected AND newer.attempt_uuid<>selected.attempt_uuid
                        AND (newer.result->>'Disposition')::integer=1)
                """,
                new { selected = previous.SelectedRepertoireAttemptUuid },
                t,
                cancellationToken: ct
            )
        );
        if (sourceChanged || await CapacityObservationChanged(c, t, requirement, ct))
        {
            return;
        }
        throw new ArgumentException(
            "An unchanged permanent failure requires a changed source, processor, capacity profile or observed owner."
        );
    }

    private static async Task<bool> CapacityObservationChanged(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        MusicMaterializationRetryRequirement requirement,
        CancellationToken ct
    )
    {
        if (requirement.CapacityOwner is not { } expected)
        {
            return false;
        }
        var actual = await c.QuerySingleOrDefaultAsync<NormalizationSubjectObservation>(
            new CommandDefinition(
                """
                SELECT uuid AS "SubjectUuid",desired_generation AS "DesiredGeneration",editorial_revision AS "EditorialRevision",
                    published_work_uuid AS "PublishedWorkUuid" FROM normalization.work_subjects WHERE uuid=@uuid
                """,
                new { uuid = expected.SubjectUuid },
                t,
                cancellationToken: ct
            )
        );
        if (actual != expected)
        {
            return true;
        }
        if (requirement.CapacityMembership is not { } membership)
        {
            return false;
        }
        var current = await NormalizationWorkService.ReadInputContributionCapacityStampAsync(
            c,
            t,
            expected.SubjectUuid,
            ct
        );
        return current != membership;
    }
}
