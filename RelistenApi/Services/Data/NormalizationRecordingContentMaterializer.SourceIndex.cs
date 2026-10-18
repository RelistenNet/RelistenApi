using System;
using System.Collections.Generic;
using System.Linq;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationRecordingContentMaterializer
{
    /// <summary>
    /// Indexes the authenticated complete source once. File identities, proposal coordinates and
    /// original text stay separate; large medleys must not repeatedly scan every other source claim.
    /// </summary>
    private sealed class RecordingSourceIndex
    {
        private readonly IReadOnlyDictionary<string, RepertoireResolutionClaim> claims;
        private readonly Dictionary<
            (Guid Input, Guid Proposal),
            NormalizationRecordingTitleProposal
        > proposals = [];
        private readonly Dictionary<(Guid Input, Guid Track), NormalizationFileBinding> files = [];
        private readonly Dictionary<
            (Guid Input, Guid Proposal, int Position),
            TrackTitleSegment
        > segments = [];

        public RecordingSourceIndex(CompletedRepertoireConsumption consumption)
        {
            claims = consumption.Source.Claims.ToDictionary(claim => claim.CorrespondenceKey);
            var batch = consumption.Recording!;
            foreach (var input in NormalizationRecordingReadSet.Inputs(batch.Preparation))
            {
                foreach (var file in input.Binding.Files.Where(file => file.Binding.Value != null))
                {
                    files.Add(
                        (input.Binding.InputRevisionUuid, file.Binding.Value!.TrackUuid),
                        file
                    );
                }
            }
            var groups = new[]
            {
                new NormalizationRecordingInputExtraction(
                    batch.Preparation.Binding.InputRevisionUuid,
                    batch.Extraction
                ),
            }.Concat(batch.AdditionalExtractions ?? []);
            foreach (var input in groups)
            {
                foreach (var proposal in input.Extraction.Titles)
                {
                    proposals.Add((input.InputRevisionUuid, proposal.ProposalUuid), proposal);
                    foreach (var segment in proposal.Extraction?.Segments ?? [])
                    {
                        segments.Add(
                            (input.InputRevisionUuid, proposal.ProposalUuid, segment.Position),
                            segment
                        );
                    }
                }
            }
        }

        public NormalizationRecordingTitleProposal Proposal(MusicUsageClaimReference reference) =>
            proposals[(reference.InputRevisionUuid, reference.ProposalUuid!.Value)];

        public TrackTitleSegment? Segment(MusicUsageClaimReference reference) =>
            reference.SegmentPosition is { } position
                ? segments[(reference.InputRevisionUuid, reference.ProposalUuid!.Value, position)]
                : null;

        public RecordingContentSourceReference Reference(MusicUsageClaimReference reference)
        {
            var key =
                "recording:"
                + reference.ProposalUuid!.Value.ToString("D")
                + (reference.SegmentPosition is { } position ? ":segment:" + position : "");
            var claim = claims[key];
            var file = files[(reference.InputRevisionUuid, reference.TrackUuid!.Value)];
            return new(
                reference.ProcessingAttemptUuid,
                reference.InputRevisionUuid,
                reference.ProposalUuid.Value,
                reference.TrackUuid.Value,
                reference.SegmentPosition ?? 0,
                file.Observation.Origin,
                claim.ClaimUuid,
                claim.CorrespondenceKey
            );
        }
    }
}
