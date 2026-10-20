using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationCandidateService
{
    /// <summary>One claim's complete current factual projection, shared by page decisions and final negative rechecks.</summary>
    private static async Task<RepertoireResolutionClaimContext> ReadRepertoireClaimContext(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid registrationUuid,
        Guid? artistUuid,
        RepertoireResolutionClaim claim,
        IReadOnlyList<MusicPublication> exclusions,
        CancellationToken ct,
        RepertoireTitleIndex? titleIndex = null,
        RepertoireKnowledgeIndex? knowledge = null,
        Guid? ignoreAssociationAttempt = null
    )
    {
        var association = await ReadRepertoireAssociation(
            c,
            t,
            registrationUuid,
            claim.CorrespondenceKey,
            ct,
            skipAttempt: ignoreAssociationAttempt
        );
        var assignments = await ReadRepertoireImportedAssignments(c, t, claim.ImportedSongUuid, ct);
        if (artistUuid is not { } artist)
        {
            return new(claim, null, association, assignments, [], [], exclusions);
        }
        var claimFacts = new CandidateFactsContext();
        var exactIdentities = await ReadRepertoireClaimIdentities(c, t, artist, claim, ct);
        var history = knowledge?.History(c, t, artist, ct);
        var independent =
            history == null
                ? null
                : await history.ReadAsync(association, assignments, exactIdentities);
        if (history != null)
        {
            await history.ObserveOwnersAsync(claimFacts);
        }
        var retainedAssociation = independent == null ? association : independent.Association;
        claimFacts.AddArtist(artist);
        if (titleIndex is { Complete: false })
        {
            claimFacts.AddIssue(
                CandidateFactsIssueKind.CandidateLimit,
                factKind: "artist_title_graph"
            );
        }
        var query = RepertoireClaimQuery(artist, claim, claimFacts);
        var expertEvaluation = knowledge?.Evaluate(claim, artist);
        var expertTargets =
            knowledge == null
                ? []
                : await knowledge.TargetsAsync(c, t, artist, expertEvaluation!, ct);
        await ObserveRepertoireKnowledgeOwners(c, t, expertEvaluation, claimFacts, ct);
        var selection =
            query.Names.Count + query.ExternalIdentities.Count == 0
                ? new NormalizationCandidateSet(IdentityMatchingDomain.Repertoire, [], false)
                : await FindInSnapshotAsync(c, t, query, ValidateQuery(query), ct);
        var forced = assignments
            .Where(assignment => !assignment.Retracted)
            .Select(assignment => assignment.RepertoireSongUuid)
            .Concat(retainedAssociation?.RepertoireSongUuid is { } previous ? [previous] : [])
            .Concat(
                independent?.Effects?.IndependentImportedAssignments.Select(value =>
                    value.RepertoireSongUuid
                ) ?? []
            )
            .Concat(
                independent
                    ?.Effects?.IndependentExternalIdentities.Where(value =>
                        value.RepertoireSongUuid.HasValue
                    )
                    .Select(value => value.RepertoireSongUuid!.Value)
                    ?? []
            )
            .Concat(titleIndex?.Targets(artist, query.Names) ?? [])
            .Concat(expertTargets.SelectMany(target => target.RepertoireSongUuids))
            .Distinct()
            .ToArray();
        selection = await IncludeRepertoireTargets(c, t, artist, selection, forced, claimFacts, ct);
        var limits = new CandidateFactsLimits();
        var selected = selection;
        if (selection.Items.Count > limits.MaximumCandidates)
        {
            claimFacts.AddIssue(CandidateFactsIssueKind.CandidateLimit);
            selected = selection with
            {
                Items = selection.Items.Take(limits.MaximumCandidates).ToArray(),
            };
        }
        var facts = await ReadRepertoireFactsAsync(c, t, selected, limits, claimFacts, ct);
        facts = facts
            .Select(fact =>
                fact with
                {
                    Header = fact.Header with
                    {
                        MetadataReviewed = fact.Titles.Any(title =>
                            title.Provenance.StoredIdentityUuid == fact.Header.Uuid
                            && title.Provenance.Basis == "reviewed"
                        ),
                    },
                }
            )
            .ToArray();
        if (
            claim.CorrespondenceKey.StartsWith("provider:", StringComparison.Ordinal)
            && claim.CorrespondenceKey.Contains(":duplicate:", StringComparison.Ordinal)
        )
        {
            // The authenticated provider reader preserves every duplicate occurrence under
            // this explicit key suffix; none may bootstrap the shared qualified identity.
            claimFacts.AddIssue(
                CandidateFactsIssueKind.InvalidStoredFact,
                factKind: "duplicate_provider_identity"
            );
        }
        if (independent?.Effects is { } expired)
        {
            facts = RemoveExpiredRepertoireIdentities(facts, expired);
        }
        if (assignments.Length > 0 || exactIdentities.Length > 0)
        {
            await ReadRepertoireRelationshipOwners(
                c,
                t,
                assignments
                    .Select(value => value.RepertoireSongUuid)
                    .Concat(
                        exactIdentities
                            .Where(value => value.RepertoireSongUuid.HasValue)
                            .Select(value => value.RepertoireSongUuid!.Value)
                    )
                    .Distinct()
                    .ToArray(),
                claimFacts,
                ct
            );
        }
        var universes = await ReadFactsUniversesAsync(c, t, query, ct);
        var read = await claimFacts.ReadReadSetAsync(c, t, universes, ct);
        var snapshot = new NormalizationCandidateFactsSnapshot<RepertoireCandidateFacts>(
            FactsVersion,
            FactsQueryHash(query, limits),
            query,
            limits,
            selection,
            facts,
            read,
            claimFacts.Issues
        );
        var ids = facts
            .SelectMany(fact => fact.IdentityClosure)
            .Select(header => header.Uuid)
            .Distinct()
            .ToArray();
        var songs = await ReadRepertoireSongs(c, t, ids, ct);
        var identities = (await ReadRepertoireExternalIdentities(c, t, ids, ct))
            .Concat(exactIdentities)
            .DistinctBy(value => value.Uuid)
            .ToArray();
        return new(
            claim,
            snapshot,
            retainedAssociation,
            assignments,
            songs,
            identities,
            exclusions,
            expertTargets,
            independent?.Effects,
            independent?.ExpertSupported ?? false
        );
    }
}
