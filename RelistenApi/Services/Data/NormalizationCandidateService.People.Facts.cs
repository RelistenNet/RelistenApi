using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationCandidateService
{
    private static async Task<PersonCandidateFacts[]> ReadPeopleFactsAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        NormalizationCandidateSet selection,
        CandidateFactsLimits limits,
        CandidateFactsContext context,
        CancellationToken ct
    )
    {
        var facts = new List<PersonCandidateFacts>();
        foreach (var hit in selection.Items)
        {
            var identities = await ReadPersonIdentities(c, t, hit.Uuid, limits, context, ct);
            var members = identities.Select(identity => identity.Uuid).ToArray();
            var aliases = await ReadPersonAliases(c, t, hit.Uuid, members, limits, context, ct);
            var identifiers = await ReadPersonIdentifiers(
                c,
                t,
                hit.Uuid,
                members,
                limits,
                context,
                ct
            );
            var credits = await ReadPersonCreditContexts(
                c,
                t,
                hit.Uuid,
                members,
                limits,
                context,
                ct
            );
            var actors = await ReadPersonActorEvidence(
                c,
                t,
                hit.Uuid,
                members,
                limits,
                context,
                ct
            );

            facts.Add(
                new PersonCandidateFacts
                {
                    Hit = hit,
                    Header = identities.Single(identity => identity.Uuid == hit.Uuid).Header,
                    IdentityClosure = identities.Select(identity => identity.Header).ToArray(),
                    Redirects = identities
                        .Where(identity => identity.SurvivorUuid.HasValue)
                        .Select(identity => new CandidateRedirectFact(
                            identity.Uuid,
                            identity.SurvivorUuid!.Value,
                            identity.DecisionUuid
                        ))
                        .ToArray(),
                    Aliases = aliases.Witnesses,
                    AcceptedAliasMatchingNames = aliases.MatchingNames,
                    AliasMatchingNamesComplete = aliases.MatchingComplete,
                    AliasAssertionCount = aliases.AssertionCount,
                    AliasSupportCount = aliases.SupportCount,
                    AliasEvidenceTruncated = aliases.WitnessesTruncated,
                    ExternalIdentities = identifiers,
                    ArtistContexts = credits.Artists,
                    MissingSourceCreditCount = credits.MissingSource,
                    MissingArtistCreditCount = credits.MissingArtist,
                    MissingContextWitnesses = credits.MissingWitnesses,
                    MissingContextWitnessesTruncated = credits.MissingWitnessesTruncated,
                    ActorEvidence = actors.Witnesses,
                    ActorMatchingFacts = actors.MatchingFacts,
                    ActorContextCount = actors.ContextCount,
                    ActorEvidenceTruncated = actors.WitnessesTruncated,
                }
            );
        }
        return facts.ToArray();
    }

    private static async Task<PersonFactIdentityRow[]> ReadPersonIdentities(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid candidate,
        CandidateFactsLimits limits,
        CandidateFactsContext context,
        CancellationToken ct
    )
    {
        // Bound the recursive UUID stream before joining wider rows. The terminal row is
        // fetched explicitly as well: SQL does not promise the recursive stream's order.
        var rows = (
            await c.QueryAsync<PersonFactIdentityRow>(
                new CommandDefinition(
                    """
                    WITH RECURSIVE members(uuid) AS (
                        SELECT @candidate::uuid
                        UNION SELECT r.retired_uuid FROM members m
                            JOIN normalization.person_redirects r ON r.survivor_uuid=m.uuid
                    ), bounded AS MATERIALIZED (SELECT uuid FROM members LIMIT @take)
                    SELECT p.uuid AS "Uuid",p.display_name AS "DisplayText",p.revision AS "Revision",
                        p.kind AS "Kind",p.metadata_reviewed AS "MetadataReviewed",owner.uuid AS "OwnerSubjectUuid",
                        r.survivor_uuid AS "SurvivorUuid",r.decision_uuid AS "DecisionUuid"
                    FROM bounded m JOIN normalization.people p ON p.uuid=m.uuid
                    LEFT JOIN normalization.person_redirects r ON r.retired_uuid=p.uuid
                    LEFT JOIN normalization.work_subjects owner ON owner.domain='people'
                        AND owner.subject_kind='person' AND owner.subject_uuid=p.uuid
                    """,
                    new { candidate, take = limits.MaximumIdentityClosure + 1 },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        if (rows.Length > limits.MaximumIdentityClosure)
        {
            context.AddIssue(CandidateFactsIssueKind.IdentityClosureLimit, candidate, "person");
        }
        var terminal =
            rows.SingleOrDefault(row => row.Uuid == candidate)
            ?? await c.QuerySingleAsync<PersonFactIdentityRow>(
                new CommandDefinition(
                    """
                    SELECT p.uuid AS "Uuid",p.display_name AS "DisplayText",p.revision AS "Revision",
                        p.kind AS "Kind",p.metadata_reviewed AS "MetadataReviewed",owner.uuid AS "OwnerSubjectUuid",
                        NULL::uuid AS "SurvivorUuid",NULL::uuid AS "DecisionUuid"
                    FROM normalization.people p LEFT JOIN normalization.work_subjects owner ON owner.domain='people'
                        AND owner.subject_kind='person' AND owner.subject_uuid=p.uuid WHERE p.uuid=@candidate
                    """,
                    new { candidate },
                    t,
                    cancellationToken: ct
                )
            );
        var retained = rows.Where(row => row.Uuid != candidate)
            .Take(limits.MaximumIdentityClosure - 1)
            .Append(terminal)
            .OrderBy(row => row.Uuid)
            .ToArray();
        foreach (var identity in retained)
        {
            context.AddOwner(identity.OwnerSubjectUuid, candidate);
        }
        return retained;
    }

    private static async Task<CandidateExternalIdentityFact[]> ReadPersonIdentifiers(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid candidate,
        Guid[] members,
        CandidateFactsLimits limits,
        CandidateFactsContext context,
        CancellationToken ct
    )
    {
        var rows = (
            await c.QueryAsync<PersonFactIdentifierRow>(
                new CommandDefinition(
                    """
                    SELECT a.uuid AS "Uuid",a.person_uuid AS "StoredIdentityUuid",a.provider AS "Provider",
                        a.resource_type AS "ResourceType",a.provider_key AS "ProviderKey",
                        owner.uuid AS "OwnerSubjectUuid",a.revision AS "Revision",a.basis AS "Basis",
                        a.input_revision_uuid AS "InputRevisionUuid",a.json_pointer AS "JsonPointer",
                        a.start_code_point AS "StartCodePoint",a.end_code_point AS "EndCodePoint",a.quote AS "Quote"
                    FROM normalization.person_external_identities a LEFT JOIN normalization.work_subjects owner ON owner.domain='people'
                        AND owner.subject_kind='person' AND owner.subject_uuid=a.person_uuid
                    WHERE a.person_uuid=ANY(@members) AND a.state='accepted' ORDER BY a.uuid LIMIT @take
                    """,
                    new { members, take = limits.MaximumAssertionsPerKind + 1 },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        if (rows.Length > limits.MaximumAssertionsPerKind)
        {
            context.AddIssue(
                CandidateFactsIssueKind.AssertionLimit,
                candidate,
                "person_external_identity"
            );
        }
        if (rows.Length != 0)
        {
            // Neither an account handle nor authority over a person is encoded by these rows.
            context.AddIssue(
                CandidateFactsIssueKind.UnknownAuthority,
                candidate,
                "person_external_identity"
            );
        }
        return rows.Take(limits.MaximumAssertionsPerKind)
            .Select(row => new CandidateExternalIdentityFact(
                row.Provenance,
                row.Provider,
                row.ResourceType,
                row.ProviderKey
            ))
            .ToArray();
    }

    private static async Task<PersonCreditContexts> ReadPersonCreditContexts(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid candidate,
        Guid[] members,
        CandidateFactsLimits limits,
        CandidateFactsContext context,
        CancellationToken ct
    )
    {
        // Artist membership is an exact aggregate, independent of audio availability and
        // witness limits. Do not join citation rows here: one assertion may have many citations.
        var counts = (
            await c.QueryAsync<PersonArtistCountRow>(
                new CommandDefinition(
                    """
                    SELECT a.uuid AS "ArtistUuid",count(*) AS "Count"
                    FROM normalization.recording_credits credit
                    JOIN sources s ON s.uuid=credit.source_uuid JOIN artists a ON a.id=s.artist_id
                    WHERE credit.person_uuid=ANY(@members) AND credit.state='accepted' AND a.uuid IS NOT NULL
                    GROUP BY a.uuid ORDER BY a.uuid LIMIT @take
                    """,
                    new { members, take = limits.MaximumArtistContexts + 1 },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        if (counts.Length > limits.MaximumArtistContexts)
        {
            context.AddIssue(
                CandidateFactsIssueKind.ArtistContextLimit,
                candidate,
                "recording_credit"
            );
        }
        var artists = counts.Take(limits.MaximumArtistContexts).ToArray();
        var artistUuids = artists.Select(row => row.ArtistUuid).ToArray();
        foreach (var artist in artistUuids)
        {
            context.AddArtist(artist);
        }
        var groups = (
            await ReadPersonCreditGroups(c, t, members, artistUuids, limits, ct)
        ).ToLookup(row => row.ArtistUuid);
        var witnesses = (
            await ReadPersonArtistWitnesses(c, t, members, artistUuids, limits, ct)
        ).ToLookup(row => row.ArtistUuid);
        var witnessFacts = (
            await BuildPersonCreditWitnesses(
                c,
                t,
                candidate,
                witnesses
                    .SelectMany(group => group.Take(limits.MaximumWitnessesPerArtist))
                    .ToArray(),
                limits,
                context,
                ct
            )
        ).ToLookup(witness => witness.ArtistUuid);
        var artistFacts = new List<PersonArtistContextFact>();
        foreach (var artist in artists)
        {
            var grouped = groups[artist.ArtistUuid].ToArray();
            if (grouped.Length > limits.MaximumRoleBasisGroupsPerArtist)
            {
                context.AddIssue(
                    CandidateFactsIssueKind.RoleBasisGroupLimit,
                    candidate,
                    "recording_credit"
                );
            }
            artistFacts.Add(
                new(
                    artist.ArtistUuid,
                    artist.Count,
                    grouped
                        .Take(limits.MaximumRoleBasisGroupsPerArtist)
                        .Select(row => new CreditRoleBasisCount(row.Role, row.Basis, row.Count))
                        .ToArray(),
                    witnessFacts[artist.ArtistUuid].ToArray(),
                    witnesses[artist.ArtistUuid].Count() > limits.MaximumWitnessesPerArtist
                )
            );
        }

        var missing = await c.QuerySingleAsync<PersonMissingCreditCounts>(
            new CommandDefinition(
                """
                SELECT count(*) FILTER(WHERE s.uuid IS NULL) AS "MissingSource",
                    count(*) FILTER(WHERE s.uuid IS NOT NULL AND a.uuid IS NULL) AS "MissingArtist"
                FROM normalization.recording_credits credit LEFT JOIN sources s ON s.uuid=credit.source_uuid
                LEFT JOIN artists a ON a.id=s.artist_id
                WHERE credit.person_uuid=ANY(@members) AND credit.state='accepted'
                """,
                new { members },
                t,
                cancellationToken: ct
            )
        );
        var missingExamples = await ReadPersonMissingWitnesses(c, t, members, limits, ct);
        var missingWitnesses = await BuildPersonCreditWitnesses(
            c,
            t,
            candidate,
            missingExamples.Take(limits.MaximumMissingContextWitnesses).ToArray(),
            limits,
            context,
            ct
        );
        if (missing.MissingSource != 0 || missing.MissingArtist != 0)
        {
            context.AddIssue(
                CandidateFactsIssueKind.MissingCatalogContext,
                candidate,
                "recording_credit"
            );
        }
        return new(
            artistFacts.ToArray(),
            missing.MissingSource,
            missing.MissingArtist,
            missingWitnesses,
            missingExamples.Length > limits.MaximumMissingContextWitnesses
        );
    }

    private static async Task<PersonCreditGroupRow[]> ReadPersonCreditGroups(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid[] members,
        Guid[] artists,
        CandidateFactsLimits limits,
        CancellationToken ct
    ) =>
        (
            await c.QueryAsync<PersonCreditGroupRow>(
                new CommandDefinition(
                    """
                    WITH grouped AS (
                        SELECT a.uuid AS artist_uuid,credit.role,credit.basis,count(*) AS count
                        FROM normalization.recording_credits credit JOIN sources s ON s.uuid=credit.source_uuid
                        JOIN artists a ON a.id=s.artist_id
                        WHERE credit.person_uuid=ANY(@members) AND credit.state='accepted' AND a.uuid=ANY(@artists)
                        GROUP BY a.uuid,credit.role,credit.basis
                    ), ranked AS (
                        SELECT *,row_number() OVER(PARTITION BY artist_uuid ORDER BY role,basis) AS rank FROM grouped
                    )
                    SELECT artist_uuid AS "ArtistUuid",role AS "Role",basis AS "Basis",count AS "Count"
                        FROM ranked WHERE rank<=@take ORDER BY artist_uuid,role,basis
                    """,
                    new
                    {
                        members,
                        artists,
                        take = limits.MaximumRoleBasisGroupsPerArtist + 1,
                    },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();

    private static async Task<PersonCreditWitnessRow[]> ReadPersonArtistWitnesses(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid[] members,
        Guid[] artists,
        CandidateFactsLimits limits,
        CancellationToken ct
    ) =>
        (
            await c.QueryAsync<PersonCreditWitnessRow>(
                new CommandDefinition(
                    """
                    WITH ranked AS (
                        SELECT credit.uuid,a.uuid AS artist_uuid,row_number() OVER(PARTITION BY a.uuid
                            ORDER BY CASE credit.basis WHEN 'reviewed' THEN 0 ELSE 1 END,credit.uuid) AS rank
                        FROM normalization.recording_credits credit JOIN sources s ON s.uuid=credit.source_uuid
                        JOIN artists a ON a.id=s.artist_id
                        WHERE credit.person_uuid=ANY(@members) AND credit.state='accepted' AND a.uuid=ANY(@artists)
                    )
                    SELECT credit.uuid AS "Uuid",credit.source_uuid AS "SourceUuid",credit.person_uuid AS "StoredIdentityUuid",
                        credit.role AS "Role",credit.original_text AS "OriginalText",credit.scope_track_uuid AS "ScopeTrackUuid",
                        credit.revision AS "Revision",credit.basis AS "Basis",credit.reason_code AS "ReasonCode",
                        ranked.artist_uuid AS "ArtistUuid",'Artist' AS "ContextStatus"
                    FROM ranked JOIN normalization.recording_credits credit ON credit.uuid=ranked.uuid
                    WHERE ranked.rank<=@take ORDER BY ranked.artist_uuid,ranked.rank
                    """,
                    new
                    {
                        members,
                        artists,
                        take = limits.MaximumWitnessesPerArtist + 1,
                    },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();

    private static async Task<PersonCreditWitnessRow[]> ReadPersonMissingWitnesses(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid[] members,
        CandidateFactsLimits limits,
        CancellationToken ct
    ) =>
        (
            await c.QueryAsync<PersonCreditWitnessRow>(
                new CommandDefinition(
                    """
                    SELECT credit.uuid AS "Uuid",credit.source_uuid AS "SourceUuid",credit.person_uuid AS "StoredIdentityUuid",
                        credit.role AS "Role",credit.original_text AS "OriginalText",credit.scope_track_uuid AS "ScopeTrackUuid",
                        credit.revision AS "Revision",credit.basis AS "Basis",credit.reason_code AS "ReasonCode",
                        NULL::uuid AS "ArtistUuid",
                        CASE WHEN s.uuid IS NULL THEN 'MissingSource' ELSE 'MissingArtist' END AS "ContextStatus"
                    FROM normalization.recording_credits credit LEFT JOIN sources s ON s.uuid=credit.source_uuid
                    LEFT JOIN artists a ON a.id=s.artist_id
                    WHERE credit.person_uuid=ANY(@members) AND credit.state='accepted' AND a.uuid IS NULL
                    ORDER BY credit.uuid LIMIT @take
                    """,
                    new { members, take = limits.MaximumMissingContextWitnesses + 1 },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();

    private static async Task<RecordingCreditWitness[]> BuildPersonCreditWitnesses(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid candidate,
        PersonCreditWitnessRow[] rows,
        CandidateFactsLimits limits,
        CandidateFactsContext context,
        CancellationToken ct
    )
    {
        if (rows.Length == 0)
        {
            return [];
        }
        // A popular taper may have many artist memberships. Fetch the bounded examples'
        // owners and current citations in batches rather than issuing requests per artist.
        var owners = (
            await c.QueryAsync<PersonCreditOwnerRow>(
                new CommandDefinition(
                    """
                    SELECT subject_uuid AS "SourceUuid",uuid AS "OwnerSubjectUuid"
                    FROM normalization.work_subjects WHERE domain='people' AND subject_kind='recording'
                        AND subject_uuid=ANY(@sources)
                    """,
                    new { sources = rows.Select(row => row.SourceUuid).Distinct().ToArray() },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToDictionary(row => row.SourceUuid, row => (Guid?)row.OwnerSubjectUuid);
        var citations = (
            await c.QueryAsync<PersonCreditEvidenceRow>(
                new CommandDefinition(
                    """
                    SELECT requested.credit_uuid AS "CreditUuid",e.input_revision_uuid AS "InputRevisionUuid",
                        e.json_pointer AS "JsonPointer",e.start_code_point AS "StartCodePoint",
                        e.end_code_point AS "EndCodePoint",e.quote AS "Quote"
                    FROM unnest(@creditUuids::uuid[],@revisions::bigint[]) requested(credit_uuid,revision)
                    CROSS JOIN LATERAL (
                        SELECT DISTINCT input_revision_uuid,json_pointer,start_code_point,end_code_point,quote
                        FROM normalization.recording_credit_evidence
                        WHERE credit_uuid=requested.credit_uuid AND credit_revision=requested.revision
                        ORDER BY input_revision_uuid,json_pointer,start_code_point NULLS FIRST,end_code_point NULLS FIRST,quote NULLS FIRST
                        LIMIT @take
                    ) e
                    ORDER BY requested.credit_uuid,e.input_revision_uuid,e.json_pointer,e.start_code_point NULLS FIRST,e.end_code_point NULLS FIRST,e.quote NULLS FIRST
                    """,
                    new
                    {
                        creditUuids = rows.Select(row => row.Uuid).ToArray(),
                        revisions = rows.Select(row => row.Revision).ToArray(),
                        take = limits.MaximumEvidencePerAssertion + 1,
                    },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToLookup(row => row.CreditUuid);
        var result = new List<RecordingCreditWitness>();
        foreach (var row in rows)
        {
            var owner = owners.GetValueOrDefault(row.SourceUuid);
            context.AddOwner(owner, candidate);
            var evidence = citations[row.Uuid].Select(citation => citation.Reference).ToArray();
            if (evidence.Length > limits.MaximumEvidencePerAssertion)
            {
                context.AddIssue(
                    CandidateFactsIssueKind.EvidenceLimit,
                    candidate,
                    "recording_credit"
                );
            }
            if (evidence.Length == 0)
            {
                context.AddIssue(
                    CandidateFactsIssueKind.InvalidStoredFact,
                    candidate,
                    "recording_credit_evidence"
                );
            }
            result.Add(
                new(
                    new(
                        row.Uuid,
                        row.StoredIdentityUuid,
                        owner,
                        row.Revision,
                        row.Basis,
                        evidence.Take(limits.MaximumEvidencePerAssertion).ToArray()
                    ),
                    row.SourceUuid,
                    row.Role,
                    row.OriginalText,
                    row.ScopeTrackUuid,
                    row.ReasonCode,
                    Enum.Parse<CandidateCreditContextStatus>(row.ContextStatus),
                    row.ArtistUuid
                )
            );
        }
        return result.ToArray();
    }

    private sealed record PersonFactIdentityRow(
        Guid Uuid,
        string DisplayText,
        long Revision,
        string Kind,
        bool MetadataReviewed,
        Guid? OwnerSubjectUuid,
        Guid? SurvivorUuid,
        Guid? DecisionUuid
    )
    {
        public CandidateIdentityHeader Header =>
            new(Uuid, DisplayText, Revision, OwnerSubjectUuid, Kind, MetadataReviewed);
    }

    private abstract class PersonFactAssertionRow
    {
        public Guid Uuid { get; init; }
        public Guid StoredIdentityUuid { get; init; }
        public Guid? OwnerSubjectUuid { get; init; }
        public long Revision { get; init; }
        public string Basis { get; init; } = null!;
        public Guid InputRevisionUuid { get; init; }
        public string JsonPointer { get; init; } = null!;
        public int? StartCodePoint { get; init; }
        public int? EndCodePoint { get; init; }
        public string? Quote { get; init; }
        public CandidateAssertionProvenance Provenance =>
            new(
                Uuid,
                StoredIdentityUuid,
                OwnerSubjectUuid,
                Revision,
                Basis,
                [new(InputRevisionUuid, JsonPointer, StartCodePoint, EndCodePoint, Quote)]
            );
    }

    private sealed class PersonFactIdentifierRow : PersonFactAssertionRow
    {
        public string Provider { get; init; } = null!;
        public string ResourceType { get; init; } = null!;
        public string ProviderKey { get; init; } = null!;
    }

    private sealed record PersonCreditOwnerRow(Guid SourceUuid, Guid OwnerSubjectUuid);

    private sealed record PersonCreditEvidenceRow(
        Guid CreditUuid,
        Guid InputRevisionUuid,
        string JsonPointer,
        int? StartCodePoint,
        int? EndCodePoint,
        string? Quote
    )
    {
        public NormalizedEvidenceReference Reference =>
            new(InputRevisionUuid, JsonPointer, StartCodePoint, EndCodePoint, Quote);
    }

    private sealed record PersonArtistCountRow(Guid ArtistUuid, long Count);

    private sealed record PersonCreditGroupRow(
        Guid ArtistUuid,
        string Role,
        string Basis,
        long Count
    );

    private sealed record PersonMissingCreditCounts(long MissingSource, long MissingArtist);

    private sealed record PersonCreditWitnessRow(
        Guid Uuid,
        Guid SourceUuid,
        Guid StoredIdentityUuid,
        string Role,
        string OriginalText,
        Guid? ScopeTrackUuid,
        long Revision,
        string Basis,
        string ReasonCode,
        Guid? ArtistUuid,
        string ContextStatus
    );

    private sealed record PersonCreditContexts(
        PersonArtistContextFact[] Artists,
        long MissingSource,
        long MissingArtist,
        RecordingCreditWitness[] MissingWitnesses,
        bool MissingWitnessesTruncated
    );
}
