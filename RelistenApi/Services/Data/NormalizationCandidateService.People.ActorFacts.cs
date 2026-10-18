using System;
using System.Collections.Generic;
using System.Data;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationCandidateService
{
    // The total read is intentionally not byte-capped: all active accounts must contribute
    // matching membership and owner clocks. One authenticated record is materialized at a
    // time. Total database work and owner-stamp growth still need workload measurement.
    private const int MaximumActorRecordBytes = 32 * 1024 * 1024;

    private static async Task<PersonActorAccounts> ReadPersonActorEvidence(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid candidate,
        Guid[] members,
        CandidateFactsLimits limits,
        CandidateFactsContext context,
        CancellationToken ct
    )
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT a.uuid,a.support_uuid,a.credit_uuid,a.credit_revision,a.credit_basis,a.person_uuid,
                a.source_uuid,a.owner_subject_uuid,owner.uuid AS current_owner_uuid,
                a.contributor_provider,a.contributor_type,a.contributor_key,a.extraction_attempt_uuid,
                a.proposal_uuid,a.input_revision_uuid,a.application_attempt_uuid,a.introduced_work_uuid,
                a.basis,a.credited_name,a.facts_hash,octet_length(a.facts::text)::bigint AS evidence_bytes,
                CASE WHEN octet_length(a.facts::text)<=@maximumRecordBytes THEN a.facts::text ELSE NULL END AS facts_json
            FROM normalization.recording_actor_contexts a
            JOIN normalization.recording_credit_contributor_support support ON support.uuid=a.support_uuid
            JOIN normalization.recording_credits credit ON credit.uuid=a.credit_uuid
            LEFT JOIN normalization.work_subjects owner ON owner.domain='people' AND owner.subject_kind='recording'
                AND owner.subject_uuid=a.source_uuid
            WHERE a.person_uuid=ANY(@members) AND credit.person_uuid=ANY(@members)
                AND a.retired_work_uuid IS NULL AND support.retired_work_uuid IS NULL
                AND support.credit_uuid=credit.uuid AND credit.state='accepted' AND credit.source_uuid=a.source_uuid
            ORDER BY a.uuid
            """,
            c,
            t
        );
        command.Parameters.AddWithValue("members", members);
        command.Parameters.AddWithValue("maximumRecordBytes", MaximumActorRecordBytes);
        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SequentialAccess,
            ct
        );
        var matching = new PeopleActorMatchingAccumulator(
            limits.MaximumAssertionsPerKind,
            limits.MaximumActorMatchingBytes!.Value
        );
        var witnesses = new List<CandidateActorEvidenceFact>();
        long count = 0;
        long witnessBytes = 0;
        while (await reader.ReadAsync(ct))
        {
            count++;
            // Read metadata in column order before the potentially large sequential JSON
            // value. It establishes every original owner even when no raw witness can fit.
            var row = ActorContextRow.ReadMetadata(reader);
            context.AddOwner(row.OwnerSubjectUuid, candidate);
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
            if (reader.IsDBNull(21))
            {
                context.AddIssue(
                    CandidateFactsIssueKind.EvidenceLimit,
                    candidate,
                    "recording_actor_record_bytes"
                );
                continue;
            }
            var json = reader.GetString(21);
            var facts = ReadActorFacts(json, row.FactsHash, candidate, context);
            if (facts == null)
            {
                continue;
            }
            matching.Add(row.CreditedName, facts);

            // Witness omission never removes semantic membership. Keep each retained raw
            // account intact, including citations, instead of clipping its original evidence.
            if (
                witnesses.Count >= limits.MaximumActorContexts
                || row.EvidenceBytes > limits.MaximumActorEvidenceBytes - witnessBytes
            )
            {
                continue;
            }
            witnesses.Add(row.ToFact(facts));
            witnessBytes += row.EvidenceBytes;
        }
        var aggregate = matching.Build();
        foreach (var kind in matching.OmittedKinds)
        {
            context.AddIssue(
                kind == "bytes"
                    ? CandidateFactsIssueKind.EvidenceLimit
                    : CandidateFactsIssueKind.AssertionLimit,
                candidate,
                "recording_actor_matching_" + kind
            );
        }
        return new(aggregate, witnesses.ToArray(), count, witnesses.Count < count);
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

    private sealed record PersonActorAccounts(
        PersonActorMatchingFacts MatchingFacts,
        CandidateActorEvidenceFact[] Witnesses,
        long ContextCount,
        bool WitnessesTruncated
    );

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
        long EvidenceBytes
    )
    {
        public static ActorContextRow ReadMetadata(NpgsqlDataReader reader) =>
            new(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetGuid(2),
                reader.GetInt64(3),
                reader.GetString(4),
                reader.GetGuid(5),
                reader.GetGuid(6),
                reader.GetGuid(7),
                reader.IsDBNull(8) ? null : reader.GetGuid(8),
                reader.GetString(9),
                reader.GetString(10),
                reader.GetString(11),
                reader.GetGuid(12),
                reader.GetGuid(13),
                reader.GetGuid(14),
                reader.GetGuid(15),
                reader.GetGuid(16),
                reader.GetString(17),
                reader.GetString(18),
                reader.GetString(19),
                reader.GetInt64(20)
            );

        public CandidateActorEvidenceFact ToFact(
            CreditedActorFacts<NormalizedEvidenceReference> facts
        ) =>
            new(
                ContextUuid,
                SupportUuid,
                CreditUuid,
                CreditRevision,
                CreditBasis,
                StoredPersonUuid,
                SourceUuid,
                OwnerSubjectUuid,
                new(Provider, ResourceType, ProviderKey),
                ExtractionAttemptUuid,
                ProposalUuid,
                InputRevisionUuid,
                ApplicationAttemptUuid,
                IntroducedWorkUuid,
                Basis,
                CreditedName,
                facts
            );
    }
}
