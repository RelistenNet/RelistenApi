using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationCandidateService
{
    // This is the existing people membership capacity, not a larger Work default. A refused
    // complete owner union blocks acceptance; no retained subset can stand for absent owners.
    private const int MaximumAliasSupportOwners = 100000;

    private static async Task<PersonAliasFacts> ReadPersonAliases(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid candidate,
        Guid[] members,
        CandidateFactsLimits limits,
        CandidateFactsContext context,
        CancellationToken ct
    )
    {
        // Every active support contributes freshness before distinct wording or witness
        // bounds. The identity closure already contributes original and resolved people.
        var support = await ReadPersonAliasSupport(c, t, candidate, members, context, ct);
        var counts = await c.QuerySingleAsync<PersonAliasCounts>(
            new CommandDefinition(
                """
                WITH aliases AS MATERIALIZED (
                    SELECT wording FROM normalization.current_person_aliases
                    WHERE person_uuid=ANY(@members)
                ), names AS (
                    SELECT DISTINCT wording COLLATE "C" AS wording FROM aliases
                )
                SELECT (SELECT count(*) FROM aliases) AS "Assertions",
                    count(*) AS "Names",(2+coalesce(sum(6::bigint*octet_length(wording)+3),0))::bigint AS "EncodedNameBytesUpperBound"
                FROM names
                """,
                new { members },
                t,
                cancellationToken: ct
            )
        );
        // Six encoded bytes per UTF-8 byte plus JSON separators conservatively covers
        // System.Text.Json escaping before any wording is materialized in this process.
        var matchingComplete = true;
        if (counts.Names > limits.MaximumAliasMatchingNames!.Value)
        {
            context.AddIssue(
                CandidateFactsIssueKind.AssertionLimit,
                candidate,
                "person_alias_matching_names"
            );
            matchingComplete = false;
        }
        if (counts.EncodedNameBytesUpperBound > limits.MaximumAliasMatchingBytes!.Value)
        {
            context.AddIssue(
                CandidateFactsIssueKind.EvidenceLimit,
                candidate,
                "person_alias_matching_bytes"
            );
            matchingComplete = false;
        }
        string[] names = [];
        if (matchingComplete)
        {
            // PostgreSQL C collation preserves exact spelling. The final ordinal sort also
            // gives .NET's specified ordering for supplementary Unicode characters.
            names = (
                await c.QueryAsync<string>(
                    new CommandDefinition(
                        """
                        SELECT DISTINCT wording COLLATE "C" FROM normalization.current_person_aliases
                        WHERE person_uuid=ANY(@members)
                        """,
                        new { members },
                        t,
                        cancellationToken: ct
                    )
                )
            ).Order(StringComparer.Ordinal).ToArray();
            if (names.LongLength != counts.Names)
            {
                context.AddIssue(
                    CandidateFactsIssueKind.InvalidStoredFact,
                    candidate,
                    "person_alias_matching_names"
                );
                matchingComplete = false;
            }
        }
        var witnesses = await ReadPersonAliasWitnesses(c, t, members, limits, ct);
        return new(
            names,
            matchingComplete,
            witnesses,
            counts.Assertions,
            support,
            witnesses.LongLength < counts.Assertions
        );
    }

    private static async Task<long> ReadPersonAliasSupport(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid candidate,
        Guid[] members,
        CandidateFactsContext context,
        CancellationToken ct
    )
    {
        // Bound UUIDs before materializing them. Never load assertion/scope JSON just to
        // collect owners. Structural proof checks follow without transferring raw JSON;
        // the future accepted writer must also authenticate extraction and policy semantics.
        var owners = (
            await c.QueryAsync<Guid>(
                new CommandDefinition(
                    """
                    WITH active AS MATERIALIZED (
                        SELECT assignment.source_resolution_owner_uuid,assignment.extraction_owner_uuid
                        FROM normalization.current_person_aliases alias
                        JOIN normalization.person_alias_contributor_support support ON support.alias_uuid=alias.uuid
                        JOIN normalization.source_actor_alias_assignments assignment ON assignment.uuid=support.assignment_uuid
                        WHERE alias.person_uuid=ANY(@members) AND support.retired_work_uuid IS NULL
                            AND assignment.retired_work_uuid IS NULL
                    ), owners AS (
                        SELECT source_resolution_owner_uuid AS uuid FROM active
                        UNION SELECT extraction_owner_uuid FROM active
                    )
                    SELECT uuid FROM owners ORDER BY uuid LIMIT @take
                    """,
                    new { members, take = MaximumAliasSupportOwners + 1 },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        if (owners.Length > MaximumAliasSupportOwners)
        {
            context.AddIssue(
                CandidateFactsIssueKind.AssertionLimit,
                candidate,
                "person_alias_support_owners"
            );
        }
        foreach (var owner in owners.Take(MaximumAliasSupportOwners))
        {
            context.AddOwner(owner, candidate);
        }
        var counts = await ReadPersonAliasSupportCounts(c, t, members, ct);
        if (counts.InvalidProofs != 0)
        {
            context.AddIssue(
                CandidateFactsIssueKind.InvalidStoredFact,
                candidate,
                "person_alias_support_proof"
            );
        }
        return counts.Supports;
    }

    private static Task<PersonAliasSupportCounts> ReadPersonAliasSupportCounts(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid[] members,
        CancellationToken ct
    ) =>
        c.QuerySingleAsync<PersonAliasSupportCounts>(
            new CommandDefinition(
                """
                SELECT count(*) AS "Supports",count(*) FILTER(WHERE (
                    length(assignment.original_assertion->>'Version')>0
                    AND assignment.original_assertion->>'AssertionUuid'=assignment.assertion_uuid::text
                    AND assignment.original_assertion->>'ActorReferenceUuid'=assignment.actor_reference_uuid::text
                    AND assignment.original_assertion->>'FieldSha256'=assignment.field_sha256
                    AND jsonb_typeof(assignment.original_assertion->'Name')='string'
                    AND length(assignment.original_assertion->>'Name')>0
                    AND jsonb_typeof(assignment.original_assertion->'NameEvidence')='object'
                    AND assignment.original_assertion#>>'{NameEvidence,InputRevisionUuid}'=assignment.input_revision_uuid::text
                    AND assignment.original_assertion#>>'{NameEvidence,JsonPointer}'=assignment.field_json_pointer
                    AND jsonb_typeof(assignment.original_assertion->'Aliases')='array'
                    AND assignment.original_assertion->'Aliases'<>'[]'::jsonb
                    AND jsonb_typeof(assignment.original_assertion->'Relationships')='array'
                    AND assignment.original_assertion->'Relationships'<>'[]'::jsonb
                    AND jsonb_typeof(assignment.original_assertion->'Context')='object'
                    AND jsonb_typeof(assignment.original_assertion#>'{Context,Heading}')='object'
                    AND jsonb_typeof(assignment.original_assertion#>'{Context,Qualifiers}')='array'
                    AND jsonb_typeof(assignment.original_assertion#>'{Context,Statement}')='object'
                    AND jsonb_typeof(assignment.original_assertion->'Unresolved')='array'
                    AND assignment.original_scope->>'ProducerVersion'=assignment.producer_version
                    AND assignment.original_scope->>'InputRevisionUuid'=assignment.input_revision_uuid::text
                    AND assignment.original_scope#>>'{Resource,Provider}'=resource.provider
                    AND assignment.original_scope#>>'{Resource,ResourceType}'=resource.resource_type
                    AND assignment.original_scope#>>'{Resource,ProviderKey}'=resource.provider_key
                    AND jsonb_typeof(assignment.original_scope->'RecordingOrigin')='object'
                    AND jsonb_typeof(assignment.original_scope->'Extraction')='object'
                    AND jsonb_typeof(assignment.original_scope#>'{Extraction,Assertions}')='array'
                    AND (assignment.original_scope#>'{Extraction,Assertions}') @> jsonb_build_array(assignment.original_assertion)
                    AND jsonb_typeof(assignment.original_scope#>'{Extraction,Unresolved}')='array'
                    AND jsonb_typeof(assignment.original_scope#>'{Extraction,Scopes}')='array'
                ) IS NOT TRUE) AS "InvalidProofs"
                FROM normalization.current_person_aliases alias
                JOIN normalization.person_alias_contributor_support support ON support.alias_uuid=alias.uuid
                JOIN normalization.source_actor_alias_assignments assignment ON assignment.uuid=support.assignment_uuid
                JOIN normalization.input_resources resource ON resource.uuid=assignment.resource_uuid
                WHERE alias.person_uuid=ANY(@members) AND support.retired_work_uuid IS NULL
                    AND assignment.retired_work_uuid IS NULL
                """,
                new { members },
                t,
                cancellationToken: ct
            )
        );

    private static async Task<CandidateNameFact[]> ReadPersonAliasWitnesses(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid[] members,
        CandidateFactsLimits limits,
        CancellationToken ct
    ) => (
            await c.QueryAsync<PersonFactAliasRow>(
                new CommandDefinition(
                    """
                    WITH bounded AS MATERIALIZED (
                        SELECT a.*,octet_length(to_jsonb(a)::text)::bigint AS evidence_bytes
                        FROM normalization.current_person_aliases a
                        WHERE a.person_uuid=ANY(@members) ORDER BY a.uuid LIMIT @take
                    ), budgeted AS (
                        SELECT *,sum(evidence_bytes) OVER(ORDER BY uuid) AS cumulative_bytes FROM bounded
                    )
                    SELECT a.uuid AS "Uuid",a.person_uuid AS "StoredIdentityUuid",a.wording AS "Wording",
                        owner.uuid AS "OwnerSubjectUuid",a.revision AS "Revision",a.basis AS "Basis",
                        a.input_revision_uuid AS "InputRevisionUuid",a.json_pointer AS "JsonPointer",
                        a.start_code_point AS "StartCodePoint",a.end_code_point AS "EndCodePoint",a.quote AS "Quote"
                    FROM budgeted a LEFT JOIN normalization.work_subjects owner ON owner.domain='people'
                        AND owner.subject_kind='person' AND owner.subject_uuid=a.person_uuid
                    WHERE a.cumulative_bytes<=@maximumEvidenceBytes ORDER BY a.uuid
                    """,
                    new
                    {
                        members,
                        take = limits.MaximumAssertionsPerKind,
                        maximumEvidenceBytes = limits.MaximumAliasEvidenceBytes!.Value,
                    },
                    t,
                    cancellationToken: ct
                )
            )
        ).Select(row => new CandidateNameFact(row.Provenance, row.Wording)).ToArray();

    private sealed class PersonFactAliasRow : PersonFactAssertionRow
    {
        public string Wording { get; init; } = null!;
    }

    private sealed record PersonAliasSupportCounts(long Supports, long InvalidProofs);

    private sealed record PersonAliasCounts(
        long Assertions,
        long Names,
        long EncodedNameBytesUpperBound
    );

    private sealed record PersonAliasFacts(
        string[] MatchingNames,
        bool MatchingComplete,
        CandidateNameFact[] Witnesses,
        long AssertionCount,
        long SupportCount,
        bool WitnessesTruncated
    );
}
