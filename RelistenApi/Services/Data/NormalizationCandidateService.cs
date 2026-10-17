using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

/// <summary>
/// Finds plausible identities through indexed accepted names and qualified identifiers.
/// It never accepts a match or writes a work subject. Resolve returned identities again after
/// BeginWorkAsync captures dependencies; a candidate score is not identity evidence.
/// </summary>
public sealed class NormalizationCandidateService(DbService db)
{
    public async Task<NormalizationCandidateSet> FindAsync(
        NormalizationCandidateQuery query,
        CancellationToken cancellationToken = default
    )
    {
        var names = ValidateQuery(query);
        var domain = DomainTables.For(query.Domain);

        // One primary snapshot keeps names, redirects and current revisions consistent. The
        // transaction-local threshold cannot leak through a pooled connection to another job.
        await using var connection = db.CreateConnection(false, false);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken
        );
        await connection.ExecuteAsync(
            new CommandDefinition(
                "SET TRANSACTION READ ONLY; SELECT set_config('pg_trgm.strict_word_similarity_threshold',@threshold,true)",
                new
                {
                    threshold = query.MinimumNameSimilarity.ToString(CultureInfo.InvariantCulture),
                },
                transaction,
                cancellationToken: cancellationToken
            )
        );

        RawHit[] nameHits =
            names.Length == 0
                ? []
                : (
                    await connection.QueryAsync<RawHit>(
                        new CommandDefinition(
                            BuildNameQuery(domain),
                            new
                            {
                                names,
                                artistUuid = query.ArtistUuid,
                                take = query.NameLimit + 1,
                            },
                            transaction,
                            cancellationToken: cancellationToken
                        )
                    )
                ).ToArray();
        var identifierHits = await FindIdentifiers(
            connection,
            transaction,
            domain,
            query,
            cancellationToken
        );
        var raw = nameHits.Concat(identifierHits).ToArray();
        var current = await ResolveTargets(connection, transaction, domain, raw, cancellationToken);
        var result = BuildResult(query, raw, current);

        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private static string[] ValidateQuery(NormalizationCandidateQuery query)
    {
        if (
            query.NameLimit is < 1 or > 200
            || double.IsNaN(query.MinimumNameSimilarity)
            || query.MinimumNameSimilarity is < 0 or >= 1
            || query.Names.Count > 16
            || query.ExternalIdentities.Count > 16
        )
        {
            throw new ArgumentException(
                "Use at most 16 names/identifiers, a 1–200 name limit and a similarity cutoff in [0,1)."
            );
        }
        var repertoire = query.Domain == IdentityMatchingDomain.Repertoire;
        if (
            (repertoire && (query.ArtistUuid is null || query.ArtistUuid == Guid.Empty))
            || (!repertoire && query.ArtistUuid != null)
        )
        {
            throw new ArgumentException(
                "Repertoire requires an artist UUID; global domains have no artist restriction."
            );
        }

        var names = query
            .Names.Select(name => NormalizedPeopleService.NameKey(name.Trim()))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (
            names.Any(name =>
                name.EnumerateRunes().Count() is < 1 or > 200 || !name.Any(char.IsLetterOrDigit)
            )
        )
        {
            throw new ArgumentException(
                "Candidate names must contain letters or digits and use at most 200 Unicode characters."
            );
        }
        if (
            query.ExternalIdentities.Any(id =>
                string.IsNullOrWhiteSpace(id.Provider)
                || string.IsNullOrWhiteSpace(id.ResourceType)
                || string.IsNullOrWhiteSpace(id.ProviderKey)
            )
        )
        {
            throw new ArgumentException(
                "Identifiers require provider, resource type and exact provider key."
            );
        }
        if (names.Length == 0 && query.ExternalIdentities.Count == 0)
        {
            throw new ArgumentException("Supply a name or a qualified identifier.");
        }
        return names;
    }

    private static string BuildNameQuery(DomainTables domain)
    {
        // Each lateral scan uses the GiST distance order and a bounded lookahead. Multiple
        // aliases for one identity cannot silently hide a truncated pool after deduplication.
        // Do not add a UUID tie-breaker here: it forces a scan of every equal-distance name.
        // Tied pools can vary; truncation is explicit and forbids treating the pool as complete.
        return string.Join(
            "\nUNION ALL\n",
            domain.Names.Select(source =>
                $"""
                    SELECT hits.origin_uuid AS "OriginUuid",hits.score AS "Score",'{source.Reason}' AS "Reason",
                        count(*) OVER(PARTITION BY input.name)>=@take AS "PoolTruncated"
                    FROM unnest(@names::text[]) input(name)
                    CROSS JOIN LATERAL (
                        SELECT x.{source.EntityColumn} AS origin_uuid,
                            strict_word_similarity(input.name,x.{source.KeyColumn})::double precision AS score
                        FROM normalization.{source.Table} x
                        WHERE {source.Condition} AND input.name <<% x.{source.KeyColumn}
                        ORDER BY input.name <<<-> x.{source.KeyColumn}
                        LIMIT @take
                    ) hits
                    """
            )
        );
    }

    private static async Task<RawHit[]> FindIdentifiers(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        DomainTables domain,
        NormalizationCandidateQuery query,
        CancellationToken ct
    )
    {
        if (query.ExternalIdentities.Count == 0)
        {
            return [];
        }
        var identities = JsonSerializer.Serialize(
            query.ExternalIdentities.Select(id => new
            {
                provider = id.Provider.ToLowerInvariant(),
                resource_type = id.ResourceType,
                provider_key = id.ProviderKey,
            })
        );
        return (
            await c.QueryAsync<RawHit>(
                new CommandDefinition(
                    $"""
                    SELECT DISTINCT x.{domain.IdentifierEntityColumn} AS "OriginUuid",0::double precision AS "Score",
                        'provider_identity' AS "Reason",false AS "PoolTruncated"
                    FROM jsonb_to_recordset(@identities::jsonb) AS i(provider text,resource_type text,provider_key text)
                    JOIN normalization.{domain.IdentifierTable} x ON lower(x.provider)=i.provider
                        AND x.resource_type=i.resource_type AND x.provider_key=i.provider_key
                    WHERE {domain.IdentifierCondition}
                    """,
                    new { identities, artistUuid = query.ArtistUuid },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
    }

    private static async Task<TargetRow[]> ResolveTargets(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        DomainTables domain,
        IReadOnlyList<RawHit> raw,
        CancellationToken ct
    )
    {
        if (raw.Count == 0)
        {
            return [];
        }
        var origins = raw.Select(hit => hit.OriginUuid).Distinct().ToArray();
        var next = domain.Music
            ? $"SELECT p.original_uuid,r.redirect_uuid FROM paths p JOIN normalization.{domain.EntityTable} r ON r.uuid=p.uuid WHERE r.redirect_uuid IS NOT NULL"
            : $"SELECT p.original_uuid,r.survivor_uuid FROM paths p JOIN normalization.{domain.RedirectTable} r ON r.retired_uuid=p.uuid";
        var terminal = domain.Music
            ? "x.redirect_uuid IS NULL AND NOT x.retracted"
            : $"NOT EXISTS(SELECT 1 FROM normalization.{domain.RedirectTable} r WHERE r.retired_uuid=x.uuid)";
        var revisionJoin = domain.Music
            ? "JOIN normalization.music_revisions revision ON revision.uuid=x.revision_uuid"
            : "";
        var revision = domain.Music ? "revision.revision" : "x.revision";
        return (
            await c.QueryAsync<TargetRow>(
                new CommandDefinition(
                    $"""
                    WITH RECURSIVE paths(original_uuid,uuid) AS (
                        SELECT uuid,uuid FROM unnest(@origins::uuid[]) origin(uuid)
                        UNION {next}
                    )
                    SELECT p.original_uuid AS "OriginUuid",x.uuid AS "Uuid",{revision} AS "Revision",
                        x.{domain.DisplayColumn} AS "DisplayText",owner.uuid AS "OwnerSubjectUuid"
                    FROM paths p JOIN normalization.{domain.EntityTable} x ON x.uuid=p.uuid
                    {revisionJoin}
                    LEFT JOIN normalization.work_subjects owner ON owner.domain=@workDomain
                        AND owner.subject_kind=@ownerKind AND owner.subject_uuid=x.{domain.OwnerEntityColumn}
                    WHERE {terminal}
                    """,
                    new
                    {
                        origins,
                        workDomain = domain.WorkDomain,
                        ownerKind = domain.OwnerKind,
                    },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
    }

    private static NormalizationCandidateSet BuildResult(
        NormalizationCandidateQuery query,
        IReadOnlyList<RawHit> raw,
        IReadOnlyList<TargetRow> targets
    )
    {
        var hits = (
            from hit in raw
            join target in targets on hit.OriginUuid equals target.OriginUuid
            group hit by target into groupHits
            select new NormalizationCandidateHit(
                groupHits.Key.Uuid,
                groupHits.Key.Revision,
                groupHits.Key.DisplayText,
                groupHits.Key.OwnerSubjectUuid,
                groupHits.Max(hit => hit.Score),
                groupHits
                    .Select(hit => hit.Reason)
                    .Distinct()
                    .Order(StringComparer.Ordinal)
                    .ToArray()
            )
        )
            .GroupBy(hit => hit.Uuid)
            .Select(group =>
                group.First() with
                {
                    NameSimilarity = group.Max(hit => hit.NameSimilarity),
                    Reasons = group
                        .SelectMany(hit => hit.Reasons)
                        .Distinct()
                        .Order(StringComparer.Ordinal)
                        .ToArray(),
                }
            )
            .ToArray();
        var names = hits.Where(hit => hit.Reasons.Any(reason => reason != "provider_identity"))
            .OrderByDescending(hit => hit.NameSimilarity)
            .ThenBy(hit => hit.Uuid)
            .ToArray();
        var retained = names.Take(query.NameLimit).Select(hit => hit.Uuid).ToHashSet();
        var selected = hits.Where(hit =>
                retained.Contains(hit.Uuid) || hit.Reasons.Contains("provider_identity")
            )
            .OrderByDescending(hit => hit.Reasons.Contains("provider_identity"))
            .ThenByDescending(hit => hit.NameSimilarity)
            .ThenBy(hit => hit.Uuid)
            .ToArray();
        return new(
            query.Domain,
            selected,
            raw.Any(hit => hit.PoolTruncated) || names.Length > query.NameLimit
        );
    }

    private sealed class RawHit
    {
        public Guid OriginUuid { get; set; }
        public double Score { get; set; }
        public string Reason { get; set; } = "";
        public bool PoolTruncated { get; set; }
    }

    private sealed record TargetRow(
        Guid OriginUuid,
        Guid Uuid,
        long Revision,
        string DisplayText,
        Guid? OwnerSubjectUuid
    );

    private sealed record NameSource(
        string Table,
        string EntityColumn,
        string KeyColumn,
        string Condition,
        string Reason
    );

    /// <summary>Trusted typed table mappings. No caller controls SQL identifiers or accepted relationships.</summary>
    private sealed record DomainTables(
        string EntityTable,
        string? RedirectTable,
        string DisplayColumn,
        string WorkDomain,
        string OwnerKind,
        string OwnerEntityColumn,
        bool Music,
        string IdentifierTable,
        string IdentifierEntityColumn,
        string IdentifierCondition,
        IReadOnlyList<NameSource> Names
    )
    {
        public static DomainTables For(IdentityMatchingDomain domain) =>
            domain switch
            {
                IdentityMatchingDomain.People => new(
                    "people",
                    "person_redirects",
                    "display_name",
                    "people",
                    "person",
                    "uuid",
                    false,
                    "person_external_identities",
                    "person_uuid",
                    "x.state='accepted'",
                    [
                        new("people", "uuid", "name_key", "true", "name"),
                        new(
                            "person_aliases",
                            "person_uuid",
                            "name_key",
                            "x.state='accepted'",
                            "alias"
                        ),
                    ]
                ),
                IdentityMatchingDomain.Venues => new(
                    "canonical_venues",
                    "venue_redirects",
                    "display_name",
                    "venues",
                    "venue",
                    "uuid",
                    false,
                    "venue_external_identities",
                    "venue_uuid",
                    "x.state='accepted'",
                    [
                        new("canonical_venues", "uuid", "name_key", "true", "name"),
                        new("venue_names", "venue_uuid", "name_key", "x.state='accepted'", "alias"),
                    ]
                ),
                IdentityMatchingDomain.Repertoire => new(
                    "repertoire_songs",
                    null,
                    "display_title",
                    "songs",
                    "artist_repertoire",
                    "artist_uuid",
                    true,
                    "music_external_identities",
                    "repertoire_song_uuid",
                    "NOT x.retracted AND x.repertoire_song_uuid IS NOT NULL AND x.artist_uuid=@artistUuid",
                    [
                        new(
                            "repertoire_songs",
                            "uuid",
                            "candidate_names",
                            "NOT x.retracted AND x.artist_uuid=@artistUuid",
                            "music_name"
                        ),
                    ]
                ),
                IdentityMatchingDomain.Compositions => new(
                    "compositions",
                    null,
                    "display_title",
                    "compositions",
                    "composition",
                    "uuid",
                    true,
                    "music_external_identities",
                    "composition_uuid",
                    "NOT x.retracted AND x.composition_uuid IS NOT NULL",
                    [
                        new(
                            "compositions",
                            "uuid",
                            "candidate_names",
                            "NOT x.retracted",
                            "music_name"
                        ),
                    ]
                ),
                _ => throw new ArgumentOutOfRangeException(nameof(domain)),
            };
    }
}
