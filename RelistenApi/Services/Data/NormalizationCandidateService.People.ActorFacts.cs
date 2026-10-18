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

public sealed partial class NormalizationCandidateService
{
    private static async Task<CandidateActorEvidenceFact[]> ReadPersonActorEvidence(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid candidate,
        Guid[] members,
        CandidateFactsLimits limits,
        CandidateFactsContext context,
        CancellationToken ct
    )
    {
        // Read source accounts independently of bounded artist witnesses. A context follows a
        // reviewed merge through the reverse closure, but never an unrelated credit reassignment.
        var rows = (
            await c.QueryAsync<ActorContextRow>(
                new CommandDefinition(
                    """
                    WITH bounded AS MATERIALIZED (
                        SELECT a.*,owner.uuid AS current_owner_uuid,
                            octet_length(a.facts::text)::bigint AS evidence_bytes,
                            row_number() OVER(ORDER BY a.uuid) AS ordinal
                        FROM normalization.recording_actor_contexts a
                        JOIN normalization.recording_credit_contributor_support support ON support.uuid=a.support_uuid
                        JOIN normalization.recording_credits credit ON credit.uuid=a.credit_uuid
                        LEFT JOIN normalization.work_subjects owner ON owner.domain='people' AND owner.subject_kind='recording'
                            AND owner.subject_uuid=a.source_uuid
                        WHERE a.person_uuid=ANY(@members) AND credit.person_uuid=ANY(@members)
                            AND a.retired_work_uuid IS NULL AND support.retired_work_uuid IS NULL
                            AND support.credit_uuid=credit.uuid AND credit.state='accepted' AND credit.source_uuid=a.source_uuid
                        ORDER BY a.uuid LIMIT @take
                    ), sized AS (
                        SELECT *,sum(evidence_bytes) OVER(ORDER BY uuid) AS consumed_bytes FROM bounded
                    )
                    SELECT a.uuid AS "ContextUuid",a.support_uuid AS "SupportUuid",a.credit_uuid AS "CreditUuid",
                        a.credit_revision AS "CreditRevision",a.credit_basis AS "CreditBasis",a.person_uuid AS "StoredPersonUuid",
                        a.source_uuid AS "SourceUuid",a.owner_subject_uuid AS "OwnerSubjectUuid",a.current_owner_uuid AS "CurrentOwnerUuid",
                        a.contributor_provider AS "Provider",a.contributor_type AS "ResourceType",a.contributor_key AS "ProviderKey",
                        a.extraction_attempt_uuid AS "ExtractionAttemptUuid",a.proposal_uuid AS "ProposalUuid",
                        a.input_revision_uuid AS "InputRevisionUuid",a.application_attempt_uuid AS "ApplicationAttemptUuid",
                        a.introduced_work_uuid AS "IntroducedWorkUuid",a.basis AS "Basis",a.credited_name AS "CreditedName",
                        a.facts_hash AS "FactsHash",
                        CASE WHEN a.ordinal<=@maximumContexts AND a.consumed_bytes<=@maximumBytes
                            THEN a.facts::text ELSE NULL END AS "FactsJson",
                        a.ordinal<=@maximumContexts AND a.consumed_bytes>@maximumBytes AS "EvidenceBytesOmitted"
                    FROM sized a ORDER BY a.uuid
                    """,
                    new
                    {
                        members,
                        take = limits.MaximumActorContexts + 1,
                        maximumContexts = limits.MaximumActorContexts,
                        maximumBytes = limits.MaximumActorEvidenceBytes,
                    },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        if (rows.Length > limits.MaximumActorContexts)
        {
            context.AddIssue(
                CandidateFactsIssueKind.AssertionLimit,
                candidate,
                "recording_actor_context"
            );
        }
        var budget = new ActorEvidenceBudget(candidate, limits, context);
        var result = new List<CandidateActorEvidenceFact>();
        foreach (var row in rows.Take(limits.MaximumActorContexts))
        {
            context.AddOwner(row.OwnerSubjectUuid, candidate);
            if (row.EvidenceBytesOmitted)
            {
                // The server returns only a bounded prefix of JSON. Counting bytes after
                // deserialization would already have materialized the unbounded evidence.
                context.AddIssue(
                    CandidateFactsIssueKind.EvidenceLimit,
                    candidate,
                    "recording_actor_bytes"
                );
                continue;
            }
            if (row.CurrentOwnerUuid != row.OwnerSubjectUuid)
            {
                context.AddOwner(row.CurrentOwnerUuid, candidate);
                context.AddIssue(
                    CandidateFactsIssueKind.InvalidStoredFact,
                    candidate,
                    "recording_actor_owner"
                );
                continue;
            }
            var facts = ReadActorFacts(row.FactsJson!, row.FactsHash, candidate, context);
            if (facts == null)
            {
                continue;
            }
            result.Add(
                new(
                    row.ContextUuid,
                    row.SupportUuid,
                    row.CreditUuid,
                    row.CreditRevision,
                    row.CreditBasis,
                    row.StoredPersonUuid,
                    row.SourceUuid,
                    row.OwnerSubjectUuid,
                    new(row.Provider, row.ResourceType, row.ProviderKey),
                    row.ExtractionAttemptUuid,
                    row.ProposalUuid,
                    row.InputRevisionUuid,
                    row.ApplicationAttemptUuid,
                    row.IntroducedWorkUuid,
                    row.Basis,
                    row.CreditedName,
                    budget.Project(facts)
                )
            );
        }
        return result.ToArray();
    }

    private static CreditedActorFacts<NormalizedEvidenceReference>? ReadActorFacts(
        string json,
        string expectedHash,
        Guid candidate,
        CandidateFactsContext context
    )
    {
        try
        {
            var facts = JsonSerializer.Deserialize<CreditedActorFacts<NormalizedEvidenceReference>>(
                json
            );
            if (
                facts != null
                && facts.Version == RecordingActorEvidenceExtractor.Version
                && NormalizedPeopleService.ActorFactsHash(facts) == expectedHash
            )
            {
                return facts;
            }
        }
        catch (JsonException)
        {
            // Invalid accepted rows remain a blocking diagnostic; they cannot look like a
            // successfully read empty context to an automatic identity policy.
        }
        context.AddIssue(
            CandidateFactsIssueKind.InvalidStoredFact,
            candidate,
            "recording_actor_facts"
        );
        return null;
    }

    /// <summary>Per-candidate aggregate bounds, separate from the number of recording witnesses.</summary>
    private sealed class ActorEvidenceBudget(
        Guid candidate,
        CandidateFactsLimits limits,
        CandidateFactsContext context
    )
    {
        private readonly Dictionary<string, int> used = [];

        public CreditedActorFacts<NormalizedEvidenceReference> Project(
            CreditedActorFacts<NormalizedEvidenceReference> facts
        ) =>
            facts with
            {
                Aliases = Bound(
                    facts.Aliases,
                    "aliases",
                    fact => fact with { Evidence = Evidence(fact.Evidence, "aliases") }
                ),
                Profiles = Bound(
                    facts.Profiles,
                    "profiles",
                    fact => fact with { Evidence = Evidence(fact.Evidence, "profiles") }
                ),
                Contacts = Bound(
                    facts.Contacts,
                    "contacts",
                    fact => fact with { Evidence = Evidence(fact.Evidence, "contacts") }
                ),
                Context = Bound(
                    facts.Context,
                    "context",
                    fact => fact with { Evidence = Evidence(fact.Evidence, "context") }
                ),
                Kind =
                    facts.Kind == null
                        ? null
                        : Bound(
                                [facts.Kind],
                                "kind",
                                fact => fact with { Evidence = Evidence(fact.Evidence, "kind") }
                            )
                            .SingleOrDefault(),
                Relationships = Bound(
                    facts.Relationships,
                    "relationships",
                    fact => fact with { Evidence = Evidence(fact.Evidence, "relationships") }
                ),
                Coverage = Bound(
                    facts.Coverage,
                    "coverage",
                    fact => fact with { Fields = Evidence(fact.Fields, "coverage") }
                ),
                Unresolved = Bound(
                    facts.Unresolved,
                    "unresolved",
                    fact => fact with { Evidence = Evidence(fact.Evidence, "unresolved") }
                ),
            };

        private T[] Bound<T>(IReadOnlyList<T> facts, string kind, Func<T, T> project)
        {
            var remaining = limits.MaximumAssertionsPerKind - used.GetValueOrDefault(kind);
            if (facts.Count > remaining)
            {
                context.AddIssue(
                    CandidateFactsIssueKind.AssertionLimit,
                    candidate,
                    "recording_actor_" + kind
                );
            }
            var retained = facts.Take(remaining).ToArray();
            used[kind] = used.GetValueOrDefault(kind) + retained.Length;
            return retained.Select(project).ToArray();
        }

        private NormalizedEvidenceReference[] Evidence(
            IReadOnlyList<NormalizedEvidenceReference> evidence,
            string kind
        )
        {
            if (evidence.Count > limits.MaximumEvidencePerAssertion)
            {
                context.AddIssue(
                    CandidateFactsIssueKind.EvidenceLimit,
                    candidate,
                    "recording_actor_" + kind
                );
            }
            return evidence.Take(limits.MaximumEvidencePerAssertion).ToArray();
        }
    }

    private sealed record ActorContextRow(
        Guid ContextUuid,
        Guid SupportUuid,
        Guid CreditUuid,
        long CreditRevision,
        string CreditBasis,
        Guid StoredPersonUuid,
        Guid SourceUuid,
        Guid OwnerSubjectUuid,
        Guid? CurrentOwnerUuid,
        string Provider,
        string ResourceType,
        string ProviderKey,
        Guid ExtractionAttemptUuid,
        Guid ProposalUuid,
        Guid InputRevisionUuid,
        Guid ApplicationAttemptUuid,
        Guid IntroducedWorkUuid,
        string Basis,
        string CreditedName,
        string FactsHash,
        string? FactsJson,
        bool EvidenceBytesOmitted
    );
}
