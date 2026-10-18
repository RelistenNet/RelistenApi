using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

/// <summary>
/// Reads explicit existing targets independently of candidate retrieval. Each complete reverse
/// component retains every original identity owner; redirects never rewrite the selected UUID.
/// Call in a primary snapshot, or under the complete Work owner locks when applying review.
/// </summary>
internal static class SourceActorReviewTargets
{
    private const int MaximumMembers = 100000;
    private const int QueryChunkSize = 64;

    internal static async Task<SourceActorReviewTarget> ReadAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid personUuid,
        CancellationToken ct
    ) => (await ReadManyAsync(c, t, [personUuid], ct))[0];

    /// <summary>
    /// Reads every requested original in first-request order. Acquisition shares terminal
    /// components only within this call; the caller's transaction supplies their authority.
    /// </summary>
    internal static async Task<IReadOnlyList<SourceActorReviewTarget>> ReadManyAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        IEnumerable<Guid> personUuids,
        CancellationToken ct
    )
    {
        ct.ThrowIfCancellationRequested();
        var originals = personUuids.Distinct().ToArray();
        if (originals.Length == 0)
        {
            return [];
        }
        var resolutions = await ReadResolutions(c, t, originals, ct);
        var rows = await ReadComponents(c, t, resolutions.Values.Distinct().ToArray(), ct);
        return AssembleTargets(originals, resolutions, rows, ct);
    }

    private static async Task<Dictionary<Guid, Guid>> ReadResolutions(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid[] originals,
        CancellationToken ct
    )
    {
        var result = new Dictionary<Guid, Guid>();
        foreach (var chunk in originals.Chunk(QueryChunkSize))
        {
            var rows = await c.QueryAsync<ResolutionRow>(
                new CommandDefinition(
                    """
                    SELECT original_uuid AS "OriginalUuid",resolved_uuid AS "ResolvedUuid"
                    FROM normalization.person_identity_resolution WHERE original_uuid=ANY(@originals)
                    """,
                    new { originals = chunk },
                    t,
                    cancellationToken: ct
                )
            );
            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                result.Add(row.OriginalUuid, row.ResolvedUuid);
            }
        }
        return result;
    }

    private static async Task<Dictionary<Guid, IdentityRow[]>> ReadComponents(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid[] resolvedUuids,
        CancellationToken ct
    )
    {
        var result = new Dictionary<Guid, IdentityRow[]>();
        foreach (var chunk in resolvedUuids.Chunk(QueryChunkSize))
        {
            var rows = await c.QueryAsync<IdentityRow>(
                new CommandDefinition(
                    """
                    SELECT root.resolved_uuid AS "ResolvedUuid",person.uuid AS "PersonUuid",person.revision AS "Revision",
                        redirect.survivor_uuid AS "SurvivorUuid",redirect.decision_uuid AS "DecisionUuid",
                        owner.uuid AS "OwnerUuid",owner.desired_generation AS "DesiredGeneration",
                        owner.editorial_revision AS "EditorialRevision",owner.published_work_uuid AS "PublishedWorkUuid"
                    FROM unnest(@resolvedUuids::uuid[]) root(resolved_uuid) CROSS JOIN LATERAL (
                        WITH RECURSIVE members(uuid) AS (
                            SELECT root.resolved_uuid
                            UNION SELECT redirect.retired_uuid FROM members member
                                JOIN normalization.person_redirects redirect ON redirect.survivor_uuid=member.uuid
                        )
                        SELECT uuid FROM members LIMIT @take
                    ) bounded JOIN normalization.people person ON person.uuid=bounded.uuid
                    LEFT JOIN normalization.person_redirects redirect ON redirect.retired_uuid=person.uuid
                    LEFT JOIN normalization.work_subjects owner ON owner.domain='people'
                        AND owner.subject_kind='person' AND owner.subject_uuid=person.uuid
                    ORDER BY root.resolved_uuid,person.uuid
                    """,
                    new { resolvedUuids = chunk, take = MaximumMembers + 1 },
                    t,
                    cancellationToken: ct
                )
            );
            // Each lateral recursion stops at its own sentinel. A shared recursive relation
            // could keep expanding an overflowing root while seeking another root's rows.
            // The transport chunk never supplies a smaller aggregate component limit.
            foreach (var group in rows.GroupBy(row => row.ResolvedUuid))
            {
                ct.ThrowIfCancellationRequested();
                result.Add(group.Key, group.ToArray());
            }
        }
        return result;
    }

    private static IReadOnlyList<SourceActorReviewTarget> AssembleTargets(
        Guid[] originals,
        IReadOnlyDictionary<Guid, Guid> resolutions,
        IReadOnlyDictionary<Guid, IdentityRow[]> rows,
        CancellationToken ct
    )
    {
        var components = new Dictionary<Guid, ComponentProof>();
        var result = new List<SourceActorReviewTarget>(originals.Length);
        foreach (var original in originals)
        {
            ct.ThrowIfCancellationRequested();
            if (!resolutions.TryGetValue(original, out var resolved))
            {
                throw new ArgumentException(
                    "Source review must select an existing canonical identity."
                );
            }
            if (!components.TryGetValue(resolved, out var component))
            {
                component = ReadComponentProof(resolved, rows.GetValueOrDefault(resolved, []), ct);
                components.Add(resolved, component);
            }
            if (!component.MemberUuids.Contains(original))
            {
                throw MissingOwner();
            }
            result.Add(
                new(original, resolved, component.Members, component.Redirects, component.Owners)
            );
        }
        return result;
    }

    private static ComponentProof ReadComponentProof(
        Guid resolved,
        IdentityRow[] rows,
        CancellationToken ct
    )
    {
        if (rows.Length > MaximumMembers)
        {
            throw new SourceActorReviewCapacityException(
                "source_review_target_closure_capacity_exceeded"
            );
        }
        if (rows.Length == 0 || rows.Any(row => row.OwnerUuid == null))
        {
            throw MissingOwner();
        }
        var members = new List<SourceActorReviewIdentityMember>(rows.Length);
        var redirects = new List<CandidateRedirectFact>();
        var owners = new List<NormalizationSubjectObservation>(rows.Length);
        var memberUuids = new HashSet<Guid>();
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            memberUuids.Add(row.PersonUuid);
            members.Add(new(row.PersonUuid, resolved, row.Revision));
            if (row.SurvivorUuid.HasValue)
            {
                redirects.Add(new(row.PersonUuid, row.SurvivorUuid.Value, row.DecisionUuid));
            }
            owners.Add(
                new(
                    row.OwnerUuid!.Value,
                    row.DesiredGeneration!.Value,
                    row.EditorialRevision!.Value,
                    row.PublishedWorkUuid
                )
            );
        }
        return new(
            memberUuids,
            members.ToArray(),
            redirects.ToArray(),
            owners.OrderBy(row => row.SubjectUuid).ToArray()
        );
    }

    private static NormalizationConflictException MissingOwner() =>
        new("Register every original target identity owner before source review.");

    private sealed record ComponentProof(
        IReadOnlySet<Guid> MemberUuids,
        IReadOnlyList<SourceActorReviewIdentityMember> Members,
        IReadOnlyList<CandidateRedirectFact> Redirects,
        IReadOnlyList<NormalizationSubjectObservation> Owners
    );

    private sealed record ResolutionRow(Guid OriginalUuid, Guid ResolvedUuid);

    private sealed class IdentityRow
    {
        public Guid ResolvedUuid { get; init; }
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
