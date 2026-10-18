using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace Relisten.Data;

public sealed partial class SourceActorResolutionService
{
    // The cache belongs to one preparation. Sharing an exact snapshot slot avoids copying
    // repeated names while retaining every original query result, read set and comparison.
    private sealed class CandidatePreparation(
        SourceActorResolutionSource source,
        NormalizationInputContributionsSnapshot membership,
        PeopleAcceptancePolicyState policy,
        NormalizationCandidateService candidates,
        SourceActorResolutionDecisionService decisions,
        NpgsqlConnection c,
        NpgsqlTransaction t
    )
    {
        private readonly Dictionary<string, int> cache = new(StringComparer.Ordinal);
        private readonly List<NormalizationCandidateFactsSnapshot<PersonCandidateFacts>> pools = [];

        public async Task<PreparedCandidates> BuildAsync(CancellationToken ct)
        {
            var inputs = new List<SourceActorResolutionInput>();
            foreach (var input in source.Inputs)
            {
                var scopes = new List<SourceActorResolutionScope>();
                foreach (var scope in input.Scopes)
                {
                    scopes.Add(await BuildScopeAsync(input, scope, ct));
                }
                var revision =
                    membership
                        .Contributions.SingleOrDefault(state =>
                            state.Contributor == input.OriginalInput.Contributor
                        )
                        ?.Revision
                    ?? 0;
                inputs.Add(
                    input with
                    {
                        ExpectedContributionRevision = revision,
                        Scopes = scopes.ToArray(),
                    }
                );
            }
            return new(inputs.ToArray(), pools.ToArray());
        }

        private async Task<SourceActorResolutionScope> BuildScopeAsync(
            SourceActorResolutionInput input,
            SourceActorResolutionScope scope,
            CancellationToken ct
        )
        {
            var original = scope.OriginalScope;
            var resource = source
                .Extraction.Ticket.Inputs.Single(value =>
                    value.RevisionUuid == original.InputRevisionUuid
                )
                .ResourceUuid;
            var artist = input.OriginalInput.Binding.Recording.Value!.ArtistUuid;
            var claims = new List<SourceActorResolutionClaim>();
            for (var index = 0; index < original.Extraction.Assertions.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                var assertion = original.Extraction.Assertions[index];
                var projection = PeopleActorProjection.Project(assertion, artist);
                var slot = await ReadCandidateSlotAsync(projection.Query, ct);
                var coordinates = new SourceActorAliasCoordinates(
                    source.Extraction.SourceUuid,
                    input.OriginalInput.Contributor,
                    resource,
                    original.InputRevisionUuid,
                    assertion.NameEvidence.JsonPointer,
                    original.ProducerVersion,
                    assertion.AssertionUuid,
                    assertion.ActorReferenceUuid,
                    assertion.FieldSha256
                );
                claims.Add(
                    decisions.Compare(
                        coordinates,
                        assertion,
                        index,
                        artist,
                        slot,
                        slot.HasValue ? pools[slot.Value] : null,
                        policy,
                        source.SourceReviews
                    )
                );
            }
            return scope with { Claims = claims.ToArray() };
        }

        private async Task<int?> ReadCandidateSlotAsync(
            NormalizationCandidateQuery? query,
            CancellationToken ct
        )
        {
            if (query == null)
            {
                return null;
            }
            var key = PeopleActorProjection.QueryFingerprint(query);
            if (cache.TryGetValue(key, out var existing))
            {
                return existing;
            }
            var facts = await candidates.FindPeopleFactsInSnapshotAsync(c, t, query, ct: ct);
            var added = pools.Count;
            pools.Add(facts);
            cache.Add(key, added);
            return added;
        }
    }

    private sealed record PreparedCandidates(
        IReadOnlyList<SourceActorResolutionInput> Inputs,
        IReadOnlyList<NormalizationCandidateFactsSnapshot<PersonCandidateFacts>> Pools
    );
}
