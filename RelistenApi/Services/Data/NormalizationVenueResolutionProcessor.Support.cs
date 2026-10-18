using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationVenueResolutionProcessor
{
    private async Task<VenueResolutionSupportPreparation> PrepareSupportedFactsAsync(
        VenueResolutionClaim claim,
        VenueResolutionContext context,
        IReadOnlyList<VenueResolutionSupportReference> references,
        CancellationToken ct
    )
    {
        var pool = context
            .Candidates.Facts.Concat(context.AssignmentTargets)
            .DistinctBy(value => value.Header.Uuid)
            .OrderBy(value => value.Header.Uuid)
            .ToArray();
        if (
            references.Any(value =>
                value.CandidateVenueUuid is { } target
                && !pool.Any(candidate => candidate.Header.Uuid == target)
            )
        )
        {
            throw new ArgumentException(
                "Venue support must name the source or a member of the original complete candidate pool."
            );
        }
        var revisions = references
            .SelectMany(value =>
                new[] { value.GeographyInputRevisionUuid, value.PlaceInputRevisionUuid }
            )
            .OfType<Guid>()
            .Distinct()
            .ToArray();
        var identities = await ReadVenueInputIdentitiesAsync(revisions, ct);
        var inputs = new Dictionary<Guid, NormalizationInputRevision>();
        var dependencies = new List<NormalizationInputDependency>();
        foreach (var identity in identities)
        {
            var current = await evidence.GetCurrentInputAsync(identity.Resource, ct);
            if (current == null || current.Revision.Uuid != identity.RevisionUuid)
            {
                throw new NormalizationConflictException(
                    "Venue support must retain its original current provider generation."
                );
            }
            inputs.Add(identity.RevisionUuid, current.Revision);
            dependencies.Add(new(identity.ResourceUuid, identity.RevisionUuid, current.Generation));
        }

        var intake = new NormalizationVenueSupportIntake();
        var sourceRef = references.SingleOrDefault(value => value.CandidateVenueUuid == null);
        var source = intake.PrepareSource(
            claim,
            SourceMatch(claim),
            Input(sourceRef?.GeographyInputRevisionUuid),
            Input(sourceRef?.PlaceInputRevisionUuid)
        );
        var candidates = pool.Select(candidate =>
            {
                var reference = references.SingleOrDefault(value =>
                    value.CandidateVenueUuid == candidate.Header.Uuid
                );
                return intake.PrepareCandidate(
                    candidate,
                    CandidateMatch(candidate),
                    Input(reference?.GeographyInputRevisionUuid),
                    Input(reference?.PlaceInputRevisionUuid)
                );
            })
            .ToArray();
        return new(new(source, candidates), dependencies);

        NormalizationInputRevision? Input(Guid? uuid) => uuid == null ? null : inputs[uuid.Value];
    }
}
