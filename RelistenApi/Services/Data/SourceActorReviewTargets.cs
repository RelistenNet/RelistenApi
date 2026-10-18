using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

/// <summary>
/// Reads an explicit existing target independently of candidate retrieval. The complete reverse
/// component retains every original identity owner; redirects never rewrite the selected UUID.
/// Call in a primary snapshot, or under the complete Work owner locks when applying review.
/// </summary>
internal static class SourceActorReviewTargets
{
    private const int MaximumMembers = 100000;

    internal static async Task<SourceActorReviewTarget> ReadAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid personUuid,
        CancellationToken ct
    )
    {
        var resolved =
            await c.QuerySingleOrDefaultAsync<Guid?>(
                new CommandDefinition(
                    "SELECT resolved_uuid FROM normalization.person_identity_resolution WHERE original_uuid=@personUuid",
                    new { personUuid },
                    t,
                    cancellationToken: ct
                )
            )
            ?? throw new ArgumentException(
                "Source review must select an existing canonical identity."
            );
        var rows = (
            await c.QueryAsync<IdentityRow>(
                new CommandDefinition(
                    """
                    WITH RECURSIVE members(uuid) AS (
                        SELECT @resolved::uuid
                        UNION SELECT redirect.retired_uuid FROM members member
                            JOIN normalization.person_redirects redirect ON redirect.survivor_uuid=member.uuid
                    ), bounded AS MATERIALIZED (SELECT uuid FROM members LIMIT @take)
                    SELECT person.uuid AS "PersonUuid",person.revision AS "Revision",
                        redirect.survivor_uuid AS "SurvivorUuid",redirect.decision_uuid AS "DecisionUuid",
                        owner.uuid AS "OwnerUuid",owner.desired_generation AS "DesiredGeneration",
                        owner.editorial_revision AS "EditorialRevision",owner.published_work_uuid AS "PublishedWorkUuid"
                    FROM bounded member JOIN normalization.people person ON person.uuid=member.uuid
                    LEFT JOIN normalization.person_redirects redirect ON redirect.retired_uuid=person.uuid
                    LEFT JOIN normalization.work_subjects owner ON owner.domain='people'
                        AND owner.subject_kind='person' AND owner.subject_uuid=person.uuid
                    ORDER BY person.uuid
                    """,
                    new { resolved, take = MaximumMembers + 1 },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        if (rows.Length > MaximumMembers)
        {
            throw new SourceActorReviewCapacityException(
                "source_review_target_closure_capacity_exceeded"
            );
        }
        if (
            rows.Length == 0
            || rows.All(row => row.PersonUuid != personUuid)
            || rows.Any(row => row.OwnerUuid == null)
        )
        {
            throw new NormalizationConflictException(
                "Register every original target identity owner before source review."
            );
        }
        return new(
            personUuid,
            resolved,
            rows.Select(row => new SourceActorReviewIdentityMember(
                    row.PersonUuid,
                    resolved,
                    row.Revision
                ))
                .ToArray(),
            rows.Where(row => row.SurvivorUuid.HasValue)
                .Select(row => new CandidateRedirectFact(
                    row.PersonUuid,
                    row.SurvivorUuid!.Value,
                    row.DecisionUuid
                ))
                .ToArray(),
            rows.Select(row => new NormalizationSubjectObservation(
                    row.OwnerUuid!.Value,
                    row.DesiredGeneration!.Value,
                    row.EditorialRevision!.Value,
                    row.PublishedWorkUuid
                ))
                .OrderBy(row => row.SubjectUuid)
                .ToArray()
        );
    }

    private sealed class IdentityRow
    {
        public Guid PersonUuid { get; init; }
        public long Revision { get; init; }
        public Guid? SurvivorUuid { get; init; }
        public Guid? DecisionUuid { get; init; }
        public Guid? OwnerUuid { get; init; }
        public long? DesiredGeneration { get; init; }
        public long? EditorialRevision { get; init; }
        public Guid? PublishedWorkUuid { get; init; }
    }
}
