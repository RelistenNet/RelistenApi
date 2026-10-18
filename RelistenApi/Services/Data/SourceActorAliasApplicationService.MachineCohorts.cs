using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace Relisten.Data;

public sealed partial class SourceActorAliasApplicationService
{
    private abstract record MachineCohort(PreparedSource Source);

    private sealed record SelectedMachinePerson(PreparedSource Source, Guid PersonUuid)
        : MachineCohort(Source);

    private sealed record ExhaustedMachineClaims(PreparedSource Source) : MachineCohort(Source);

    /// <summary>
    /// Owns only one RR preparation. Original winners route batching; actual current pools
    /// authorize decisions. No facts or negative observations survive into another stage.
    /// </summary>
    private sealed class MachineCohortRefresh(
        NormalizationCandidateService candidates,
        PeopleResolutionPolicyService policies,
        SourceActorResolutionDecisionService decisions,
        SourceActorResolutionBatch proposal,
        PreparedSource source
    )
    {
        private readonly List<NormalizationCandidateFactsSnapshot<PersonCandidateFacts>> pools = [];
        private readonly Dictionary<string, int> querySlots = new(StringComparer.Ordinal);
        private readonly Dictionary<
            SourceActorAliasClaimReference,
            SourceActorResolutionClaim
        > refreshed = [];
        private readonly HashSet<SourceActorAliasClaimReference> examined = [];
        private readonly Dictionary<Guid, SourceActorReviewTarget> targets =
            source.Targets.ToDictionary(value => value.OriginalPersonUuid);
        private PeopleAcceptancePolicyState? policyState;

        internal async Task<MachineCohort> ReadAsync(
            NpgsqlConnection c,
            NpgsqlTransaction t,
            SourceActorAliasApplicationIndex committed,
            CancellationToken ct
        )
        {
            var unwritten = source
                .Claims.Where(value => !committed.Claims.ContainsKey(value.Claim))
                .ToArray();
            var originalPeople = proposal
                .Preparation.Inputs.SelectMany(
                    (input, inputIndex) =>
                        input.Scopes.SelectMany(
                            (scope, scopeIndex) =>
                                scope.Claims.Select(claim =>
                                    (
                                        Position: (inputIndex, scopeIndex, claim.AssertionIndex),
                                        claim.Decision.PersonUuid
                                    )
                                )
                        )
                )
                .ToDictionary(value => value.Position, value => value.PersonUuid);

            // Preserve the first current winner in source order. A later unrelated query
            // failure can follow this independent commit; it cannot produce completion.
            foreach (var planned in unwritten)
            {
                var claim = await RefreshAsync(c, t, planned.Claim, ct);
                if (claim?.Decision.Action != SourceActorResolutionAction.MatchExisting)
                {
                    continue;
                }
                var person = claim.Decision.PersonUuid!.Value;
                var seed = planned.Claim;
                var hint = originalPeople.GetValueOrDefault(
                    (seed.InputIndex, seed.ScopeIndex, seed.AssertionIndex)
                );
                if (hint.HasValue)
                {
                    // The hint never supplies the selected UUID. Every group member still
                    // gets its own current comparison, and other winners remain read-fenced.
                    foreach (
                        var other in unwritten.Where(value =>
                            originalPeople.GetValueOrDefault(
                                (
                                    value.Claim.InputIndex,
                                    value.Claim.ScopeIndex,
                                    value.Claim.AssertionIndex
                                )
                            ) == hint
                        )
                    )
                    {
                        await RefreshAsync(c, t, other.Claim, ct);
                    }
                }
                return new SelectedMachinePerson(BuildSource(), person);
            }

            // No early stop occurred: every remaining eligible machine claim was examined.
            // Only this result may proceed to readiness, finalization and terminal outcome.
            return new ExhaustedMachineClaims(BuildSource());
        }

        private async Task<SourceActorResolutionClaim?> RefreshAsync(
            NpgsqlConnection c,
            NpgsqlTransaction t,
            SourceActorAliasClaimReference reference,
            CancellationToken ct
        )
        {
            ct.ThrowIfCancellationRequested();
            if (!examined.Add(reference))
            {
                return refreshed.GetValueOrDefault(reference);
            }
            var original = Claim(source, reference);
            var coordinates = original.Coordinates;
            var pinned = proposal.Preparation.PolicyState.Active;
            if (
                !source.Current
                || pinned == null
                || source.Reviews.Reviews.Any(value => SameField(value.Coordinates, coordinates))
                || (source.Reviews.Correspondences ?? []).Any(value =>
                    value.Current.Coordinates == coordinates
                )
            )
            {
                return null;
            }

            policyState ??= await policies.ReadStateInSnapshotAsync(c, t, ct);
            if (policyState.Active == null || !Same(policyState.Active.Policy, pinned.Policy))
            {
                throw new NormalizationConflictException(
                    "The pinned source matching policy is no longer active."
                );
            }
            int? slot = null;
            var query = original.Projection.Query;
            if (query != null)
            {
                var key = PeopleActorProjection.QueryFingerprint(query);
                if (!querySlots.TryGetValue(key, out var found))
                {
                    found = pools.Count;
                    pools.Add(await candidates.FindPeopleFactsInSnapshotAsync(c, t, query, ct: ct));
                    querySlots.Add(key, found);
                }
                slot = found;
            }
            var input = source.Inputs[reference.InputIndex];
            var assertion = input.Scopes[reference.ScopeIndex].OriginalScope.Extraction.Assertions[
                reference.AssertionIndex
            ];
            var claim = decisions.Compare(
                coordinates,
                assertion,
                reference.AssertionIndex,
                input.OriginalInput.Binding.Recording.Value!.ArtistUuid,
                slot,
                slot.HasValue ? pools[slot.Value] : null,
                policyState,
                source.Reviews
            );
            refreshed.Add(reference, claim);
            if (
                claim.Decision.Action == SourceActorResolutionAction.MatchExisting
                && claim.Decision.PersonUuid is Guid person
                && !targets.ContainsKey(person)
            )
            {
                targets.Add(person, await SourceActorReviewTargets.ReadAsync(c, t, person, ct));
            }
            return claim;
        }

        private PreparedSource BuildSource()
        {
            var proposalReference = Proposal(proposal);
            var inputs = source
                .Inputs.Select(
                    (input, inputIndex) =>
                        input with
                        {
                            Scopes = input
                                .Scopes.Select(
                                    (scope, scopeIndex) =>
                                        scope with
                                        {
                                            // Keep the complete original inventory. Unexamined claims retain the
                                            // trusted pass's deterministic null-pool value, never an old winner.
                                            Claims = scope
                                                .Claims.Select(claim =>
                                                    refreshed.GetValueOrDefault(
                                                        new(
                                                            proposalReference,
                                                            inputIndex,
                                                            scopeIndex,
                                                            claim.AssertionIndex
                                                        )
                                                    ) ?? claim
                                                )
                                                .ToArray(),
                                        }
                                )
                                .ToArray(),
                        }
                )
                .ToArray();
            var planned = source
                .Claims.Select(value =>
                {
                    if (!refreshed.TryGetValue(value.Claim, out var claim))
                    {
                        return value;
                    }
                    var selected =
                        claim.Decision.Action == SourceActorResolutionAction.MatchExisting
                            ? claim.Decision.PersonUuid
                            : null;
                    return value with
                    {
                        OriginalPersonUuid = selected,
                        Disposition =
                            selected.HasValue ? SourceActorAliasApplicationDisposition.Complete
                            : claim.Decision.Action == SourceActorResolutionAction.Review
                                ? SourceActorAliasApplicationDisposition.Review
                            : SourceActorAliasApplicationDisposition.Unresolved,
                        Reason = claim.Decision.Reason,
                    };
                })
                .ToArray();
            return source with
            {
                Inputs = inputs,
                Pools = pools.ToArray(),
                Policy = policyState?.Active,
                Targets = targets.Values.ToArray(),
                Claims = planned,
            };
        }
    }
}
