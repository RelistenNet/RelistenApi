using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace Relisten.Data;

public sealed partial class SourceActorResolutionStore
{
    // Source replay establishes original values before this phase. This validator owns only
    // exact scope/claim membership and deterministic matching replay from service-owned facts.
    private sealed class FrozenClaimValidation(
        Guid sourceUuid,
        NormalizationWorkTicket extractionTicket,
        SourceActorResolutionPreparation preparation
    )
    {
        private readonly SourceActorResolutionDecisionService decisions = new(new(), new());
        private readonly HashSet<int> usedPools = [];

        public void Validate(SourceActorResolutionInput[] original, CancellationToken ct)
        {
            var byContributor = original.ToDictionary(value => value.OriginalInput.Contributor);
            foreach (var input in preparation.Inputs)
            {
                ct.ThrowIfCancellationRequested();
                if (!byContributor.TryGetValue(input.OriginalInput.Contributor, out var saved))
                {
                    throw new ArgumentException(
                        "Resolution contains a foreign original input contributor."
                    );
                }
                ValidateInput(input, saved, ct);
            }
            if (!usedPools.SetEquals(Enumerable.Range(0, preparation.CandidatePools.Count)))
            {
                throw new ArgumentException(
                    "Candidate pools must exactly cover the consumed assertion queries."
                );
            }
        }

        private void ValidateInput(
            SourceActorResolutionInput input,
            SourceActorResolutionInput saved,
            CancellationToken ct
        )
        {
            var membership = preparation.ExpectedContributions.SingleOrDefault(value =>
                value.Contributor == input.OriginalInput.Contributor
            );
            if (
                input.ExpectedContributionRevision != (membership?.Revision ?? 0)
                || !SourceActorResolutionCheckpoint.Same(saved.OriginalInput, input.OriginalInput)
                || saved.AliasPayloadPresent != input.AliasPayloadPresent
                || !SourceActorResolutionCheckpoint.Same(saved.DecodeIssues, input.DecodeIssues)
                || saved.Scopes.Count != input.Scopes.Count
            )
            {
                throw new ArgumentException(
                    "Resolution inputs must retain their exact original bindings and alias presence."
                );
            }
            for (var index = 0; index < saved.Scopes.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                ValidateScope(input, input.Scopes[index], saved.Scopes[index], ct);
            }
        }

        private void ValidateScope(
            SourceActorResolutionInput input,
            SourceActorResolutionScope scope,
            SourceActorResolutionScope saved,
            CancellationToken ct
        )
        {
            if (
                !SourceActorResolutionCheckpoint.Same(saved.OriginalScope, scope.OriginalScope)
                || !SourceActorResolutionCheckpoint.Same(saved.Fields, scope.Fields)
                || scope.Claims.Count != saved.OriginalScope.Extraction.Assertions.Count
            )
            {
                throw new ArgumentException(
                    "Resolution scopes must retain every original assertion, field, unresolved passage and coverage value."
                );
            }
            for (var index = 0; index < scope.Claims.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                ValidateClaim(input, scope, index);
            }
        }

        private void ValidateClaim(
            SourceActorResolutionInput input,
            SourceActorResolutionScope scope,
            int index
        )
        {
            var original = scope.OriginalScope;
            var assertion = original.Extraction.Assertions[index];
            var claim = scope.Claims[index];
            var resource = extractionTicket
                .Inputs.Single(value => value.RevisionUuid == original.InputRevisionUuid)
                .ResourceUuid;
            var artist = input.OriginalInput.Binding.Recording.Value!.ArtistUuid;
            var coordinates = new SourceActorAliasCoordinates(
                sourceUuid,
                input.OriginalInput.Contributor,
                resource,
                original.InputRevisionUuid,
                assertion.NameEvidence.JsonPointer,
                original.ProducerVersion,
                assertion.AssertionUuid,
                assertion.ActorReferenceUuid,
                assertion.FieldSha256
            );
            var projection = PeopleActorProjection.Project(assertion, artist);
            var pool = ReadPool(claim.CandidatePoolIndex);
            ValidateCandidatePool(pool, projection);
            var expected = decisions.Compare(
                coordinates,
                assertion,
                index,
                artist,
                claim.CandidatePoolIndex,
                pool,
                preparation.PolicyState,
                preparation.SourceReviews
            );
            if (!SourceActorResolutionCheckpoint.Same(claim, expected))
            {
                throw new ArgumentException(
                    "Resolution claims must reproduce exact original coordinates and deterministic comparisons."
                );
            }
        }

        private NormalizationCandidateFactsSnapshot<PersonCandidateFacts>? ReadPool(int? slot)
        {
            if (!slot.HasValue)
            {
                return null;
            }
            if (slot.Value < 0 || slot.Value >= preparation.CandidatePools.Count)
            {
                throw new ArgumentException("Candidate slot is outside its exact preparation.");
            }
            usedPools.Add(slot.Value);
            return preparation.CandidatePools[slot.Value];
        }
    }
}
