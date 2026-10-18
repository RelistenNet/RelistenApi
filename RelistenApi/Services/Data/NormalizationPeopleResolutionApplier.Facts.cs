using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationPeopleResolutionApplier
{
    private async Task<NormalizationPeopleResolutionPreparation> ReadDerivedFactsAsync(
        NormalizationPeopleResolutionPreparation original,
        CancellationToken ct
    )
    {
        var context = await people.ReadCreditContextAsync(original.CreditContext.SourceUuid, ct);
        var cache = new Dictionary<
            string,
            NormalizationCandidateFactsSnapshot<PersonCandidateFacts>
        >(StringComparer.Ordinal);
        var inputs = new List<NormalizationPeopleResolutionInput>();
        foreach (var input in original.Inputs)
        {
            var claims = new List<NormalizationPeopleResolutionClaim>();
            foreach (var claim in input.Claims)
            {
                ct.ThrowIfCancellationRequested();
                NormalizationCandidateFactsSnapshot<PersonCandidateFacts>? facts = null;
                if (claim.Candidates != null)
                {
                    var key = claim.Candidates.QueryHash;
                    if (!cache.TryGetValue(key, out facts))
                    {
                        facts = await candidates.FindPeopleFactsAsync(
                            claim.Candidates.Query,
                            claim.Candidates.Limits,
                            ct
                        );
                        cache.Add(key, facts);
                    }
                }
                else if (
                    Relisten.Normalization.NormalizationPeopleResolutionDecisionService.CanQueryName(
                        claim.Proposal.Mention.Name
                    )
                )
                {
                    throw new NormalizationConflictException(
                        "A queryable claim cannot omit its candidate snapshot."
                    );
                }
                claims.Add(
                    baseline.Compare(claim.Proposal, input.OriginalInput.Binding, facts, context)
                );
            }
            inputs.Add(input with { Claims = claims.ToArray() });
        }
        return original with { CreditContext = context, Inputs = inputs.ToArray() };
    }

    private async Task RevalidateOriginalInputsAsync(
        NormalizationPeopleResolutionBatch proposal,
        CancellationToken ct
    )
    {
        var refs = proposal
            .Preparation.Inputs.SelectMany(input => input.OriginalInput.Binding.Inputs)
            .Distinct()
            .ToArray();
        var retained = (await evidence.GetInputsAsync(refs, cancellationToken: ct)).ToDictionary(
            input => input.Uuid
        );
        var roots = new Dictionary<Guid, ProviderDecodeResult>();
        foreach (var prepared in proposal.Preparation.Inputs.Select(input => input.OriginalInput))
        {
            var input = retained[prepared.Binding.InputRevisionUuid];
            var decoded = decoder.Decode(input, ct);
            if (decoded.Recordings.Count != 1)
            {
                throw new NormalizationConflictException(
                    "Recording evidence no longer has an exact supported account."
                );
            }
            NormalizationRecordingBindingSnapshot current;
            if (input.Resource.Provider == "local")
            {
                var root = retained[prepared.Binding.SupportingInputs!.Single().InputRevisionUuid];
                if (!roots.TryGetValue(root.Uuid, out var decodedRoot))
                {
                    decodedRoot =
                        Relisten.Normalization.NormalizationLocalRecordingSelection.DecodeRoot(
                            root,
                            decoder,
                            cancellationToken: ct
                        );
                    roots.Add(root.Uuid, decodedRoot);
                }
                current = await bindings.BindLocalDirectoryWithRootAsync(
                    root,
                    input,
                    decodedRoot,
                    prepared.Binding.Recording.Value!.ArtistUuid,
                    ct
                );
            }
            else if (prepared.FetchUuid is { } fetchUuid)
            {
                var capture =
                    await evidence.GetCaptureAsync(fetchUuid, ct)
                    ?? throw new NormalizationConflictException(
                        "Original refresh receipt is missing."
                    );
                current = await bindings.BindRefreshedRecordingAsync(
                    new(capture, prepared.ProducerCatalogInputs!),
                    decoded.Recordings[0],
                    cancellationToken: ct
                );
            }
            else
            {
                current = await bindings.BindRecordingAsync(
                    decoded.Recordings[0],
                    prepared.Binding.Recording.Value!.ArtistUuid,
                    ct
                );
            }
            if (!PeopleAcceptanceApplicationStore.Same(current, prepared.Binding))
            {
                throw new NormalizationConflictException(
                    "Original recording associations changed after resolution."
                );
            }
        }
    }
}
