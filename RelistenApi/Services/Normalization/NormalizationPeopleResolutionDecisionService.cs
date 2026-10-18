using System;
using System.Linq;
using System.Text;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization;

/// <summary>
/// Projects retained credit claims and complete accepted candidate facts into comparable
/// records. This baseline retains set-level uncertainty for evaluation; it activates no policy.
/// </summary>
public sealed class NormalizationPeopleResolutionDecisionService(IdentityCandidateMatcher matcher)
{
    public const string Version =
        "people-resolution-baseline-v2/"
        + PeopleActorProjection.Version
        + "/"
        + IdentityCandidateMatcher.PeopleActorFeaturesVersion;

    public NormalizationPeopleResolutionClaim Compare(
        NormalizationRecordingCreditProposal proposal,
        NormalizationRecordingBindingSnapshot binding,
        NormalizationCandidateFactsSnapshot<PersonCandidateFacts>? candidates,
        RecordingCreditContext? context = null
    )
    {
        var artist = binding.Recording.Value!.ArtistUuid;
        var file =
            proposal.FileOrigin == null
                ? null
                : binding.Files.SingleOrDefault(file =>
                    file.Observation.Origin == proposal.FileOrigin
                );
        var scope =
            file?.Binding.Status == NormalizationCatalogBindingStatus.Bound
                ? file.Binding.Value!.TrackUuid
                : (Guid?)null;
        var scopeIssue =
            proposal.FileOrigin == null ? null
            : scope.HasValue ? null
            : file?.Binding.Reason ?? "missing_file_binding";
        var projection = PeopleActorProjection.Project(proposal.Mention, artist);
        var claim = projection.Record;
        bool SameClaim(RecordingCredit credit) =>
            credit.Role == Role(proposal.Mention.Role)
            && credit.ScopeTrackUuid == scope
            && (
                credit.OriginalText == (proposal.Mention.Evidence.Quote ?? proposal.Mention.Name)
                || credit.Evidence.Any(old =>
                    SameFieldRegion(old, proposal.Mention.Evidence)
                    && context!.EvidenceInputs.Any(prior =>
                        prior.InputRevisionUuid == old.InputRevisionUuid
                        && binding.Inputs.Any(current =>
                            current.InputRevisionUuid == proposal.Mention.Evidence.InputRevisionUuid
                            && current.Resource == prior.Resource
                        )
                    )
                )
            );
        var reviewed =
            scopeIssue == null
            && context != null
            && (
                context.Credits.Any(value =>
                    value.Credit.Basis == "reviewed" && SameClaim(value.Credit)
                ) || context.ReviewedExclusions.Any(value => SameClaim(value.Claim.Credit))
            );
        var comparisons = (candidates?.Facts ?? [])
            .Select(candidate =>
                matcher.ComparePeople(claim, PeopleActorProjection.Candidate(candidate))
            )
            .ToArray();
        return new(
            proposal,
            artist,
            scope,
            scopeIssue,
            candidates,
            comparisons,
            reviewed ? new(PeopleResolutionAction.Review, "reviewed_credit_claim")
                : candidates == null
                    ? new(PeopleResolutionAction.Unresolved, "unsupported_credit_query")
                : !projection.Complete
                    ? new(PeopleResolutionAction.Unresolved, "incomplete_actor_projection")
                : Decide(scopeIssue, candidates.Complete, comparisons)
        );
    }

    private static bool SameFieldRegion(
        NormalizedEvidenceReference original,
        NormalizedEvidenceReference current
    ) =>
        original.JsonPointer == current.JsonPointer
        && (
            original.InputRevisionUuid != current.InputRevisionUuid
            || original.StartCodePoint == null
            || current.StartCodePoint == null
            || original.StartCodePoint < current.EndCodePoint
                && current.StartCodePoint < original.EndCodePoint
        );

    private static string Role(RecordingCreditRole role) =>
        role switch
        {
            RecordingCreditRole.MatrixBuilder => "matrix_builder",
            RecordingCreditRole.MasteringEngineer => "mastering_engineer",
            RecordingCreditRole.RecordingEngineer => "recording_engineer",
            _ => role.ToString().ToLowerInvariant(),
        };

    internal static bool CanQueryName(string name)
    {
        var key = Relisten.Data.NormalizedPeopleService.NameKey(name.Trim());
        return key.EnumerateRunes().Count() is >= 1 and <= 200 && key.Any(char.IsLetterOrDigit);
    }

    private static PeopleResolutionDecision Decide(
        string? scopeIssue,
        bool complete,
        IdentityCandidateComparison[] comparisons
    )
    {
        if (scopeIssue != null)
        {
            return new(PeopleResolutionAction.Unresolved, "unbound_file_scope");
        }
        if (!complete)
        {
            return new(PeopleResolutionAction.Unresolved, "incomplete_candidate_facts");
        }
        if (comparisons.Any(pair => pair.Disposition == IdentityMatchDisposition.Review))
        {
            return new(PeopleResolutionAction.Review, "conflicting_candidate_facts");
        }
        if (comparisons.Count(pair => pair.IsCandidate) > 1)
        {
            return new(PeopleResolutionAction.Review, "competing_candidates");
        }
        // An empty pool is evidence for evaluating creation, not authority to allocate a
        // global person. A single namesake likewise needs an evaluated set-level policy.
        return new(
            PeopleResolutionAction.Unresolved,
            comparisons.Any(pair => pair.IsCandidate)
                ? "matching_policy_unavailable"
                : "creation_policy_unavailable"
        );
    }
}
