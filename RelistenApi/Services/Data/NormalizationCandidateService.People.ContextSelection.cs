using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationCandidateService
{
    private static void ValidatePeopleHints(NormalizationCandidateQuery query)
    {
        if (query.PeopleHints is not { } hints)
        {
            return;
        }
        if (
            query.Domain != IdentityMatchingDomain.People
            || hints.Limit is < 1 or > 200
            || hints.Contacts.Count > 16
            || hints.Profiles.Count > 16
            || hints.Contacts.Any(contact => !PeopleActorLookupContract.Supports(contact))
            || hints.Profiles.Any(profile => !PeopleActorLookupContract.Supports(profile))
        )
        {
            throw new ArgumentException(
                "People hints require bounded nonempty keys, at most 16 contacts/profiles and a 1–200 context limit."
            );
        }
    }

    // A reviewed merge can move the credit while the original context remains immutable.
    // Both endpoints must reach the same terminal identity; an unrelated reassignment does
    // not transfer the old actor's contact, profile or alias to the new person.
    private const string SameActorContextIdentity = """
        (credit.person_uuid=a.person_uuid OR EXISTS (
            WITH RECURSIVE endpoints(side,uuid) AS (
                VALUES (0,a.person_uuid),(1,credit.person_uuid)
                UNION
                SELECT e.side,r.survivor_uuid FROM endpoints e
                    JOIN normalization.person_redirects r ON r.retired_uuid=e.uuid
            )
            SELECT 1 FROM endpoints original_path JOIN endpoints current_path
                ON original_path.uuid=current_path.uuid AND original_path.side=0 AND current_path.side=1
            WHERE NOT EXISTS(SELECT 1 FROM normalization.person_redirects r WHERE r.retired_uuid=original_path.uuid)
        ))
        """;

    private const string LiveActorContextJoins = """
        JOIN normalization.recording_actor_contexts a ON a.uuid=n.context_uuid
        JOIN normalization.recording_credit_contributor_support support ON support.uuid=a.support_uuid
        JOIN normalization.recording_credits credit ON credit.uuid=a.credit_uuid
        """;

    private const string LiveActorContextCondition = """
        a.retired_work_uuid IS NULL AND support.retired_work_uuid IS NULL
        AND support.credit_uuid=credit.uuid AND credit.state='accepted' AND credit.source_uuid=a.source_uuid
        """;

    private static async Task<RawHit[]> FindActorNames(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        NormalizationCandidateQuery query,
        string[] names,
        CancellationToken ct
    )
    {
        if (query.Domain != IdentityMatchingDomain.People || names.Length == 0)
        {
            return [];
        }
        // One person can have thousands of supporting recordings with the same wording.
        // Deduplicate origins before applying the bound so those supports cannot crowd out rivals.
        return (
            await c.QueryAsync<RawHit>(
                new CommandDefinition(
                    $"""
                    SELECT hits.origin_uuid AS "OriginUuid",hits.score AS "Score",'credited_name' AS "Reason",
                        count(*) OVER(PARTITION BY input.name)>=@take AS "PoolTruncated",false AS "ContextLookup"
                    FROM unnest(@names::text[]) input(name)
                    CROSS JOIN LATERAL (
                        SELECT a.person_uuid AS origin_uuid,max(strict_word_similarity(input.name,n.name_key))::double precision AS score
                        FROM normalization.recording_actor_context_aliases n
                        {LiveActorContextJoins}
                        WHERE {LiveActorContextCondition} AND {SameActorContextIdentity}
                            AND input.name <<% n.name_key
                        GROUP BY a.person_uuid ORDER BY score DESC,a.person_uuid LIMIT @take
                    ) hits
                    """,
                    new { names, take = query.NameLimit + 1 },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
    }

    private static async Task<RawHit[]> FindActorHints(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        NormalizationCandidateQuery query,
        CancellationToken ct
    )
    {
        if (query.Domain != IdentityMatchingDomain.People || query.PeopleHints is not { } hints)
        {
            return [];
        }
        var contacts = hints.Contacts.Select(
            (contact, index) =>
                new
                {
                    index,
                    contact_type = contact.ContactType,
                    lookup_key = contact.LookupKey,
                }
        );
        var profiles = hints.Profiles.Select(
            (profile, index) =>
                new
                {
                    index,
                    provider = profile.Provider.ToLowerInvariant(),
                    @namespace = profile.Namespace,
                    lookup_key = profile.LookupKey,
                }
        );
        var contactHits = await ReadActorHintHits(
            c,
            t,
            "recording_actor_context_contacts",
            "jsonb_to_recordset(@keys::jsonb) AS input(index integer,contact_type text,lookup_key text)",
            "n.contact_type=input.contact_type AND n.lookup_key=input.lookup_key",
            "recording_contact",
            JsonSerializer.Serialize(contacts),
            hints.Limit,
            ct
        );
        var profileHits = await ReadActorHintHits(
            c,
            t,
            "recording_actor_context_profiles",
            "jsonb_to_recordset(@keys::jsonb) AS input(index integer,provider text,namespace text,lookup_key text)",
            "n.provider=input.provider AND n.namespace=input.namespace AND n.lookup_key=input.lookup_key",
            "recording_profile",
            JsonSerializer.Serialize(profiles),
            hints.Limit,
            ct
        );
        return contactHits.Concat(profileHits).ToArray();
    }

    // SQL identifiers and predicates below come only from the two fixed typed routes above.
    // Profile relationship changes comparison semantics, not the nonexclusive retrieval pool.
    private static async Task<RawHit[]> ReadActorHintHits(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        string table,
        string inputs,
        string predicate,
        string reason,
        string keys,
        int limit,
        CancellationToken ct
    ) =>
        (
            await c.QueryAsync<RawHit>(
                new CommandDefinition(
                    $"""
                    SELECT hits.origin_uuid AS "OriginUuid",0::double precision AS "Score",@reason AS "Reason",
                        count(*) OVER(PARTITION BY input.index)>=@take AS "PoolTruncated",true AS "ContextLookup"
                    FROM {inputs}
                    CROSS JOIN LATERAL (
                        SELECT DISTINCT a.person_uuid AS origin_uuid
                        FROM normalization.{table} n
                        {LiveActorContextJoins}
                        WHERE {predicate} AND {LiveActorContextCondition} AND {SameActorContextIdentity}
                        ORDER BY a.person_uuid LIMIT @take
                    ) hits
                    """,
                    new
                    {
                        keys,
                        reason,
                        take = limit + 1,
                    },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
}
