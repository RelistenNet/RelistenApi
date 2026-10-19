using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationRecordingPipeline
{
    private async Task<VenueResolutionResult[]> ResolveVenuesAsync(
        NormalizationRecordingPipelineRequest request,
        NormalizationRecordingProposalBatch batch,
        CancellationToken ct
    )
    {
        var bindings = NormalizationRecordingReadSet
            .Inputs(batch.Preparation)
            .Select(value => value.Binding)
            .DistinctBy(value => value.InputRevisionUuid)
            .ToArray();
        var inputs = (
            await evidence.GetInputsAsync(
                bindings.SelectMany(value => value.Inputs).Distinct().ToArray(),
                new(100, 32 * 1024 * 1024, 128 * 1024 * 1024),
                ct
            )
        ).ToDictionary(value => value.Uuid);
        var references = new List<VenueShowClaimReference>();
        foreach (var binding in bindings)
        {
            ct.ThrowIfCancellationRequested();
            var input = inputs[binding.InputRevisionUuid];
            var decoded = decoder.Decode(input, ct);
            ProviderRecordingObservation recording;
            if (binding.Resource.Provider == "local")
            {
                var root = inputs[binding.SupportingInputs!.Single().InputRevisionUuid];
                recording = NormalizationLocalRecordingSelection
                    .Select(
                        input,
                        decoded,
                        binding,
                        root,
                        NormalizationLocalRecordingSelection.DecodeRoot(
                            root,
                            decoder,
                            cancellationToken: ct
                        ),
                        ct
                    )
                    .RootAccount;
            }
            else
            {
                recording = decoded.Recordings.Single();
            }
            if (recording.Venue is { } venue)
            {
                references.Add(
                    new(
                        VenueShowClaimSourceKind.Recording,
                        batch.AttemptUuid,
                        venue.Origin.Evidence.InputRevisionUuid,
                        venue.Origin.Evidence.JsonPointer
                    )
                );
            }
        }

        var results = new List<VenueResolutionResult>();
        foreach (var reference in references.Distinct())
        {
            var uuid = Child(
                request,
                "venue\n"
                    + reference.InputRevisionUuid.ToString("D")
                    + "\n"
                    + reference.VenueJsonPointer
            );
            VenueResolutionAttempt attempt;
            do
            {
                ct.ThrowIfCancellationRequested();
                attempt = await venueResolver.ResolveShowAsync(uuid, reference, ct);
            } while (attempt.Result == null);
            results.Add(attempt.Result);
        }
        return results.ToArray();
    }
}
