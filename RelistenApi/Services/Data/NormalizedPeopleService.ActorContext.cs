using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizedPeopleService
{
    /// <summary>
    /// Reads active accepted source accounts on the primary. A reviewed redirect preserves an
    /// account when its stored person and current credit resolve to the same identity; an
    /// unrelated reassignment, pending credit or retired contribution cannot expose it.
    /// Historical JSON remains retained in storage rather than being returned as current facts.
    /// </summary>
    public Task<IReadOnlyList<CandidateActorEvidenceFact>> GetRecordingActorContextsAsync(
        Guid sourceUuid,
        CancellationToken cancellationToken = default
    ) =>
        db.WithWriteConnection<IReadOnlyList<CandidateActorEvidenceFact>>(async connection =>
        {
            var c = (NpgsqlConnection)connection;
            await using var t = await c.BeginTransactionAsync(
                IsolationLevel.RepeatableRead,
                cancellationToken
            );
            await c.ExecuteAsync(
                new CommandDefinition(
                    "SET TRANSACTION READ ONLY",
                    transaction: t,
                    cancellationToken: cancellationToken
                )
            );
            var rows = await c.QueryAsync<ActorContextRow>(
                new CommandDefinition(
                    """
                    SELECT actor.uuid AS ContextUuid,actor.support_uuid AS SupportUuid,actor.credit_uuid AS CreditUuid,
                        actor.credit_revision AS CreditRevision,actor.credit_basis AS CreditBasis,
                        actor.person_uuid AS StoredPersonUuid,actor.source_uuid AS SourceUuid,
                        actor.owner_subject_uuid AS OwnerSubjectUuid,
                        actor.contributor_provider AS ContributorProvider,actor.contributor_type AS ContributorType,
                        actor.contributor_key AS ContributorKey,actor.extraction_attempt_uuid AS ExtractionAttemptUuid,
                        actor.proposal_uuid AS ProposalUuid,actor.input_revision_uuid AS InputRevisionUuid,
                        actor.application_attempt_uuid AS ApplicationAttemptUuid,actor.introduced_work_uuid AS IntroducedWorkUuid,
                        actor.basis AS Basis,actor.credited_name AS CreditedName,actor.facts::text AS FactsJson,
                        credit.person_uuid AS CurrentPersonUuid
                    FROM normalization.recording_actor_contexts actor
                    JOIN normalization.recording_credit_contributor_support support ON support.uuid=actor.support_uuid
                    JOIN normalization.recording_credits credit ON credit.uuid=actor.credit_uuid
                    WHERE actor.source_uuid=@sourceUuid AND actor.retired_work_uuid IS NULL
                        AND support.retired_work_uuid IS NULL AND credit.state='accepted' AND credit.person_uuid IS NOT NULL
                    ORDER BY actor.uuid
                    """,
                    new { sourceUuid },
                    t,
                    cancellationToken: cancellationToken
                )
            );
            var result = new List<CandidateActorEvidenceFact>();
            foreach (var row in rows)
            {
                if (
                    await ResolvePersonUuidAsync(c, t, row.StoredPersonUuid, cancellationToken)
                    == await ResolvePersonUuidAsync(c, t, row.CurrentPersonUuid, cancellationToken)
                )
                {
                    result.Add(row.ToFact());
                }
            }
            await t.CommitAsync(cancellationToken);
            return result;
        });

    // This proof is constructed only from the saved guarded stage and its durable published
    // extraction inside the existing Work callback. No public writer accepts actor-fact JSON.
    private static ActorContextPublication PrepareActorContextPublication(
        Guid workUuid,
        Guid applicationAttemptUuid,
        NormalizationRecordingProposalBatch extraction,
        PeopleAcceptanceStage stage,
        NormalizationWorkTicket ticket
    )
    {
        if (
            stage.Ticket?.WorkUuid != workUuid
            || stage.PersonUuid != null
            || stage.Reconciliation?.ExtractionAttemptUuid != extraction.AttemptUuid
        )
        {
            throw new ArgumentException(
                "Actor context requires its admitted recording credit stage."
            );
        }
        var inputs = NormalizationRecordingReadSet.Inputs(extraction.Preparation);
        var groups = new[]
        {
            new NormalizationRecordingInputExtraction(
                extraction.Preparation.Binding.InputRevisionUuid,
                extraction.Extraction
            ),
        }
            .Concat(extraction.AdditionalExtractions ?? [])
            .ToDictionary(group => group.InputRevisionUuid);
        var claims = new List<AcceptedActorContextClaim>();
        foreach (var input in stage.Facts.Inputs)
        {
            var original = inputs.Single(value =>
                value.Contributor == input.OriginalInput.Contributor
            );
            var proposals = groups[original.Binding.InputRevisionUuid].Extraction.Credits;
            foreach (var claim in input.Claims)
            {
                var decision = stage.Decisions.Single(value =>
                    value.ProposalUuid == claim.Proposal.ProposalUuid
                );
                if (
                    decision.Action != PeopleResolutionAction.MatchExisting
                    || decision.PersonUuid == null
                    || claim.ScopeIssue != null
                )
                {
                    continue;
                }
                var proposal = proposals.Single(value =>
                    value.ProposalUuid == claim.Proposal.ProposalUuid
                );
                if (JsonSerializer.Serialize(proposal) != JsonSerializer.Serialize(claim.Proposal))
                {
                    throw new ArgumentException(
                        "Accepted actor context must preserve the original published proposal."
                    );
                }
                if (proposal.Mention.ActorFacts != null)
                {
                    claims.Add(
                        new(
                            original.Contributor,
                            proposal,
                            claim.ScopeTrackUuid,
                            decision.PersonUuid.Value
                        )
                    );
                }
            }
        }
        return new(applicationAttemptUuid, extraction.AttemptUuid, ticket.SubjectUuid, claims);
    }

    private static async Task WriteAcceptedActorContexts(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid workUuid,
        ActorContextPublication? publication,
        NormalizationResourceKey contributor,
        RecordingCredit incoming,
        CreditRow actual,
        Guid supportUuid,
        NormalizedEvidenceReference supportEvidence,
        CancellationToken ct
    )
    {
        // The accepted writer may preserve a reviewed claim or turn an evaluated addition into
        // pending. Neither outcome authorizes enriching that protected or unresolved identity.
        if (
            publication == null
            || incoming.Basis != "validated_match"
            || incoming.State != "accepted"
            || actual.State != "accepted"
            || actual.PersonUuid == null
            || actual.Basis == "reviewed"
        )
        {
            return;
        }
        foreach (
            var claim in publication.Claims.Where(claim =>
                claim.Contributor == contributor
                && claim.Proposal.Mention.Evidence == supportEvidence
                && CreditRole(claim.Proposal.Mention.Role) == incoming.Role
                && claim.ScopeTrackUuid == incoming.ScopeTrackUuid
                && (
                    incoming.OriginalText == claim.Proposal.Mention.Name
                    || incoming.OriginalText == claim.Proposal.Mention.Evidence.Quote
                )
            )
        )
        {
            if (await ResolvePersonUuidAsync(c, t, claim.PersonUuid, ct) != actual.PersonUuid.Value)
            {
                continue;
            }
            var facts = claim.Proposal.Mention.ActorFacts!;
            await ValidatePublicationEvidenceAsync(
                c,
                t,
                workUuid,
                "people",
                facts.Citations().Distinct().ToArray(),
                ct
            );
            var json = JsonSerializer.Serialize(facts);
            var hash = ActorFactsHash(facts);
            var context = await c.QuerySingleAsync<StoredActorContext>(
                new CommandDefinition(
                    """
                    WITH added AS (
                        INSERT INTO normalization.recording_actor_contexts
                            (support_uuid,credit_uuid,credit_revision,credit_basis,person_uuid,source_uuid,owner_subject_uuid,
                             contributor_provider,contributor_type,contributor_key,extraction_attempt_uuid,proposal_uuid,
                             input_revision_uuid,application_attempt_uuid,introduced_work_uuid,basis,credited_name,facts_hash,facts)
                        VALUES(@supportUuid,@creditUuid,@creditRevision,@creditBasis,@personUuid,@sourceUuid,@ownerSubjectUuid,
                            @Provider,@ResourceType,@ProviderKey,@extractionAttemptUuid,@proposalUuid,@InputRevisionUuid,
                            @applicationAttemptUuid,@workUuid,'validated_match',@creditedName,@hash,@json::jsonb)
                        ON CONFLICT DO NOTHING RETURNING uuid,facts_hash
                    ) SELECT uuid AS Uuid,facts_hash AS FactsHash FROM added UNION ALL
                    SELECT uuid AS Uuid,facts_hash AS FactsHash FROM normalization.recording_actor_contexts
                    WHERE support_uuid=@supportUuid AND proposal_uuid=@proposalUuid AND person_uuid=@personUuid
                    """,
                    new
                    {
                        supportUuid,
                        creditUuid = actual.Uuid,
                        creditRevision = actual.Revision,
                        creditBasis = actual.Basis,
                        personUuid = actual.PersonUuid.Value,
                        sourceUuid = actual.SourceUuid,
                        ownerSubjectUuid = publication.OwnerSubjectUuid,
                        contributor.Provider,
                        contributor.ResourceType,
                        contributor.ProviderKey,
                        extractionAttemptUuid = publication.ExtractionAttemptUuid,
                        proposalUuid = claim.Proposal.ProposalUuid,
                        supportEvidence.InputRevisionUuid,
                        applicationAttemptUuid = publication.ApplicationAttemptUuid,
                        workUuid,
                        creditedName = claim.Proposal.Mention.Name,
                        hash,
                        json,
                    },
                    t,
                    cancellationToken: ct
                )
            );
            if (context.FactsHash != hash)
            {
                throw new NormalizationConflictException(
                    "An accepted actor context UUID cannot be rebound to different facts."
                );
            }
            await WriteActorContextLookupHints(
                c,
                t,
                context.Uuid,
                claim.Proposal.Mention.Name,
                facts,
                ct
            );
        }
    }

    private static async Task WriteActorContextLookupHints(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid contextUuid,
        string creditedName,
        CreditedActorFacts<NormalizedEvidenceReference> facts,
        CancellationToken ct
    )
    {
        // Primary credited wording and explicit aliases retrieve candidates. They remain
        // nonunique source assertions, rather than edits to the canonical alias/identifier tables.
        foreach (
            var alias in new[] { (Uuid: facts.ReferenceUuid, Wording: creditedName) }.Concat(
                facts.Aliases.Select(alias => (alias.Uuid, alias.Wording))
            )
        )
        {
            await c.ExecuteAsync(
                new CommandDefinition(
                    """
                    INSERT INTO normalization.recording_actor_context_aliases(context_uuid,fact_uuid,wording,name_key)
                    VALUES(@contextUuid,@Uuid,@Wording,@nameKey) ON CONFLICT DO NOTHING
                    """,
                    new
                    {
                        contextUuid,
                        alias.Uuid,
                        alias.Wording,
                        nameKey = NameKey(alias.Wording),
                    },
                    t,
                    cancellationToken: ct
                )
            );
        }
        foreach (var contact in facts.Contacts)
        {
            await c.ExecuteAsync(
                new CommandDefinition(
                    """
                    INSERT INTO normalization.recording_actor_context_contacts(context_uuid,fact_uuid,contact_type,lookup_key)
                    VALUES(@contextUuid,@Uuid,@ContactType,@LookupKey) ON CONFLICT DO NOTHING
                    """,
                    new
                    {
                        contextUuid,
                        contact.Uuid,
                        contact.ContactType,
                        contact.LookupKey,
                    },
                    t,
                    cancellationToken: ct
                )
            );
        }
        foreach (var profile in facts.Profiles)
        {
            await c.ExecuteAsync(
                new CommandDefinition(
                    """
                    INSERT INTO normalization.recording_actor_context_profiles(context_uuid,fact_uuid,provider,namespace,lookup_key)
                    VALUES(@contextUuid,@Uuid,@Provider,@Namespace,@LookupKey) ON CONFLICT DO NOTHING
                    """,
                    new
                    {
                        contextUuid,
                        profile.Uuid,
                        profile.Provider,
                        profile.Namespace,
                        profile.LookupKey,
                    },
                    t,
                    cancellationToken: ct
                )
            );
        }
    }

    private static async Task RetireUnsupportedActorContexts(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid sourceUuid,
        CancellationToken ct
    )
    {
        // Exact support retirement is the authority to retire its actor accounts. Incomplete
        // scopes and facts-free/pending additions never clear another contribution's context.
        await c.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE normalization.recording_actor_contexts actor SET retired_work_uuid=support.retired_work_uuid
                FROM normalization.recording_credit_contributor_support support
                WHERE actor.support_uuid=support.uuid AND actor.source_uuid=@sourceUuid
                    AND actor.retired_work_uuid IS NULL AND support.retired_work_uuid IS NOT NULL;
                DELETE FROM normalization.recording_actor_context_aliases lookup USING normalization.recording_actor_contexts actor
                    WHERE lookup.context_uuid=actor.uuid AND actor.source_uuid=@sourceUuid AND actor.retired_work_uuid IS NOT NULL;
                DELETE FROM normalization.recording_actor_context_contacts lookup USING normalization.recording_actor_contexts actor
                    WHERE lookup.context_uuid=actor.uuid AND actor.source_uuid=@sourceUuid AND actor.retired_work_uuid IS NOT NULL;
                DELETE FROM normalization.recording_actor_context_profiles lookup USING normalization.recording_actor_contexts actor
                    WHERE lookup.context_uuid=actor.uuid AND actor.source_uuid=@sourceUuid AND actor.retired_work_uuid IS NOT NULL;
                """,
                new { sourceUuid },
                t,
                cancellationToken: ct
            )
        );
    }

    internal static string ActorFactsHash(CreditedActorFacts<NormalizedEvidenceReference> facts) =>
        Convert
            .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(facts))))
            .ToLowerInvariant();

    private sealed record ActorContextPublication(
        Guid ApplicationAttemptUuid,
        Guid ExtractionAttemptUuid,
        Guid OwnerSubjectUuid,
        IReadOnlyList<AcceptedActorContextClaim> Claims
    );

    private sealed record AcceptedActorContextClaim(
        NormalizationResourceKey Contributor,
        NormalizationRecordingCreditProposal Proposal,
        Guid? ScopeTrackUuid,
        Guid PersonUuid
    );

    private sealed record StoredActorContext(Guid Uuid, string FactsHash);

    private sealed class ActorContextRow
    {
        public Guid ContextUuid { get; init; }
        public Guid SupportUuid { get; init; }
        public Guid CreditUuid { get; init; }
        public long CreditRevision { get; init; }
        public string CreditBasis { get; init; } = "";
        public Guid StoredPersonUuid { get; init; }
        public Guid SourceUuid { get; init; }
        public Guid OwnerSubjectUuid { get; init; }
        public string ContributorProvider { get; init; } = "";
        public string ContributorType { get; init; } = "";
        public string ContributorKey { get; init; } = "";
        public Guid ExtractionAttemptUuid { get; init; }
        public Guid ProposalUuid { get; init; }
        public Guid InputRevisionUuid { get; init; }
        public Guid ApplicationAttemptUuid { get; init; }
        public Guid IntroducedWorkUuid { get; init; }
        public string Basis { get; init; } = "";
        public string CreditedName { get; init; } = "";
        public string FactsJson { get; init; } = "";
        public Guid CurrentPersonUuid { get; init; }

        public CandidateActorEvidenceFact ToFact() =>
            new(
                ContextUuid,
                SupportUuid,
                CreditUuid,
                CreditRevision,
                CreditBasis,
                StoredPersonUuid,
                SourceUuid,
                OwnerSubjectUuid,
                new(ContributorProvider, ContributorType, ContributorKey),
                ExtractionAttemptUuid,
                ProposalUuid,
                InputRevisionUuid,
                ApplicationAttemptUuid,
                IntroducedWorkUuid,
                Basis,
                CreditedName,
                JsonSerializer.Deserialize<CreditedActorFacts<NormalizedEvidenceReference>>(
                    FactsJson
                )!
            );
    }
}
