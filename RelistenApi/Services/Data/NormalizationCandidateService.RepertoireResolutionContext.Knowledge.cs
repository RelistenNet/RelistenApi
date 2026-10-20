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
    /// <summary>
    /// One page's reviewed policy and authenticated target bindings. A frozen-reader key never
    /// becomes a canonical UUID. Bindings come from an explicit canonical attachment or a typed
    /// accepted imported/provider correspondence in this same primary transaction.
    /// </summary>
    private sealed class RepertoireKnowledgeIndex(ExpertKnowledgeSnapshot snapshot)
    {
        public ExpertKnowledgeSnapshot Snapshot { get; } = snapshot;
        private readonly ExpertKnowledgeResolver resolver = new(snapshot);
        private readonly Dictionary<ExpertKnowledgeTarget, IReadOnlyList<Guid>> bindings = [];

        public ExpertKnowledgeEvaluation Evaluate(RepertoireResolutionClaim claim, Guid artist) =>
            resolver.Evaluate(
                RepertoireResolutionPolicyEvaluator.KnowledgeObservation(claim, artist)
            );

        public async Task<RepertoireExpertTargetResolution[]> TargetsAsync(
            NpgsqlConnection c,
            NpgsqlTransaction t,
            Guid artist,
            ExpertKnowledgeEvaluation evaluation,
            CancellationToken ct
        )
        {
            var targets = evaluation
                .ExcludedTargets.Concat(evaluation.ConflictingTargets)
                .Concat(evaluation.Target is { } preferredTarget ? [preferredTarget] : [])
                .Distinct();
            var result = new List<RepertoireExpertTargetResolution>();
            foreach (var target in targets)
            {
                if (!bindings.TryGetValue(target, out var accepted))
                {
                    accepted = await ReadTargetsAsync(c, t, artist, target, ct);
                    bindings.Add(target, accepted);
                }
                result.Add(new(target, accepted));
            }
            return result.ToArray();
        }

        private static async Task<IReadOnlyList<Guid>> ReadTargetsAsync(
            NpgsqlConnection c,
            NpgsqlTransaction t,
            Guid artist,
            ExpertKnowledgeTarget target,
            CancellationToken ct
        )
        {
            Guid[] raw;
            if (target.Domain != "songs")
            {
                return [];
            }
            if (target.CanonicalUuid is { } canonical)
            {
                raw = [canonical];
            }
            else if (
                target.ImportedSongUuid.HasValue
                || target.Namespace == "imported_song" && Guid.TryParse(target.StableKey, out _)
            )
            {
                var imported = target.ImportedSongUuid ?? Guid.Parse(target.StableKey);
                raw = (
                    await c.QueryAsync<Guid>(
                        new CommandDefinition(
                            """
                            SELECT DISTINCT assignment.repertoire_song_uuid
                            FROM normalization.imported_song_assignments assignment
                            JOIN setlist_songs imported ON imported.uuid=assignment.imported_song_uuid
                            JOIN artists artist ON artist.id=imported.artist_id
                            WHERE assignment.imported_song_uuid=@imported AND NOT assignment.retracted
                                AND artist.uuid=@artist LIMIT 101
                            """,
                            new { imported, artist },
                            t,
                            cancellationToken: ct
                        )
                    )
                ).ToArray();
            }
            else if (
                target.Namespace == "provider_song"
                && ProviderKey(target.StableKey) is { } key
            )
            {
                raw = (
                    await c.QueryAsync<Guid>(
                        new CommandDefinition(
                            """
                            SELECT DISTINCT repertoire_song_uuid FROM normalization.music_external_identities
                            WHERE provider=@Provider AND resource_type=@ResourceType AND provider_key=@ProviderKey
                                AND artist_uuid=@artist AND repertoire_song_uuid IS NOT NULL AND NOT retracted LIMIT 101
                            """,
                            new
                            {
                                key.Provider,
                                key.ResourceType,
                                key.ProviderKey,
                                artist,
                            },
                            t,
                            cancellationToken: ct
                        )
                    )
                ).ToArray();
            }
            else
            {
                return [];
            }
            var resolved = await ResolveTargets(
                c,
                t,
                DomainTables.For(IdentityMatchingDomain.Repertoire),
                raw.Select(uuid => new RawHit { OriginUuid = uuid, Reason = "expert_knowledge" })
                    .ToArray(),
                ct
            );
            return (
                await c.QueryAsync<Guid>(
                    new CommandDefinition(
                        """
                        SELECT uuid FROM normalization.repertoire_songs WHERE uuid=ANY(@ids)
                            AND artist_uuid=@artist AND NOT retracted AND redirect_uuid IS NULL
                        """,
                        new { ids = resolved.Select(row => row.Uuid).Distinct().ToArray(), artist },
                        t,
                        cancellationToken: ct
                    )
                )
            ).ToArray();
        }

        private static NormalizationResourceKey? ProviderKey(string stableKey)
        {
            try
            {
                var parts = JsonSerializer.Deserialize<string[]>(stableKey);
                return
                    parts is { Length: 3 } && parts.All(value => !string.IsNullOrWhiteSpace(value))
                    ? new(parts[0], parts[1], parts[2])
                    : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    private static async Task<RepertoireKnowledgeIndex?> ReadRepertoireKnowledgeIndex(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid? artist,
        CancellationToken ct
    ) =>
        artist is { } uuid
            ? new(
                await NormalizationExpertKnowledgeService.ReadRepertoireSnapshotAsync(
                    c,
                    t,
                    uuid,
                    ct
                )
            )
            : null;

    private static async Task ObserveRepertoireKnowledgeOwners(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        ExpertKnowledgeEvaluation? evaluation,
        CandidateFactsContext facts,
        CancellationToken ct
    )
    {
        var rules =
            evaluation?.AppliedRules.Select(rule => rule.RuleUuid).Distinct().ToArray() ?? [];
        if (rules.Length == 0)
        {
            return;
        }
        var owners = (
            await c.QueryAsync<Guid>(
                new CommandDefinition(
                    "SELECT subject_uuid FROM normalization.expert_knowledge_rules WHERE rule_uuid=ANY(@rules)",
                    new { rules },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        foreach (var owner in owners)
        {
            facts.AddOwner(owner);
        }
        if (owners.Length != rules.Length)
        {
            facts.AddIssue(CandidateFactsIssueKind.MissingOwner, factKind: "expert_rule");
        }
    }
}
