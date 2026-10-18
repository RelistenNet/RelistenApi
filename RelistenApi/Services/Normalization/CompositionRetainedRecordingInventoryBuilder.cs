using System;
using System.Collections.Generic;
using System.Linq;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization;

/// <summary>Builds recording coverage from durable lookup checkpoints, not hydrated work membership.</summary>
public static class CompositionRetainedRecordingInventoryBuilder
{
    public static CompositionRetainedRecordingInventory Build(
        IReadOnlyList<CompositionHydrationLookup> lookups,
        IReadOnlyDictionary<Guid, ProviderDecodeResult> decodedByCommand
    )
    {
        var inputs = new List<NormalizationBindingInputReference>();
        var recordings = new List<CompositionRetainedRecording>();
        var issues = new List<string>();
        foreach (
            var lookup in lookups.Where(value =>
                value.Request.Kind == MusicBrainzLookupKind.Recording
            )
        )
        {
            var reference = lookup.Acquisition?.Result?.Input;
            // Unacquired alternatives are already named by hydration coverage issues.
            // They do not belong to a retained-input inventory or prove external absence.
            if (lookup.Acquisition?.State != MusicBrainzAcquisitionState.Completed)
            {
                continue;
            }
            if (reference == null)
            {
                issues.Add($"RecordingInputUnavailable:{lookup.CommandUuid:D}");
                continue;
            }
            inputs.Add(reference);
            var expectedResource = new NormalizationResourceKey(
                "musicbrainz.org",
                "recording",
                lookup.Request.Mbid.ToString("D")
            );
            if (reference.Resource != expectedResource || reference.InputRevisionUuid == Guid.Empty)
            {
                issues.Add($"RecordingLookupIdentityMismatch:{lookup.CommandUuid:D}");
            }
            if (!decodedByCommand.TryGetValue(lookup.CommandUuid, out var decoded))
            {
                issues.Add($"RecordingInputUnavailable:{lookup.CommandUuid:D}");
                continue;
            }
            if (
                decoded.InputRevisionUuid != reference.InputRevisionUuid
                || decoded.Resource != expectedResource
                || decoded.DecoderVersion != NormalizationProviderDecoder.Version
            )
            {
                issues.Add($"RecordingDecodeIdentityMismatch:{lookup.CommandUuid:D}");
            }
            var observations = decoded.MusicBrainzRecordings ?? [];
            if (observations.Count != 1)
            {
                issues.Add($"RecordingEntityCount:{lookup.CommandUuid:D}:{observations.Count}");
            }
            issues.AddRange(
                decoded.Issues.Select(issue =>
                    $"RecordingDecode:{lookup.CommandUuid:D}:{issue.Code}:{issue.Evidence.JsonPointer}"
                )
            );
            foreach (var observation in observations)
            {
                if (
                    observation.Origin.Evidence.InputRevisionUuid != reference.InputRevisionUuid
                    || observation.Origin.Item != expectedResource
                    || observation.Recording.Identity.Identity != expectedResource
                )
                {
                    issues.Add($"RecordingObservationIdentityMismatch:{lookup.CommandUuid:D}");
                }
                recordings.Add(new(observation, decoded.Issues));
            }
        }
        if (inputs.Select(input => input.InputRevisionUuid).Distinct().Count() != inputs.Count)
        {
            issues.Add("DuplicateRecordingInput");
        }
        return new(
            inputs,
            recordings,
            issues.Count == 0,
            issues.Distinct(StringComparer.Ordinal).ToArray()
        );
    }
}
