using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationPeopleResolutionApplier
{
    private async Task<PeopleAcceptanceStage?> PrepareIdentityStageAsync(
        PeopleAcceptanceApplication application,
        NormalizationPeopleResolutionPreparation facts,
        IReadOnlyList<PeopleAcceptanceClaimDecision> decisions,
        PeopleAcceptanceClaimDecision decision,
        CancellationToken ct
    )
    {
        var creation = decision.Creation!;
        var owner = new NormalizationSubjectKey("people", "person", creation.PersonUuid);
        var membership = await work.GetInputContributionsAsync(owner, cancellationToken: ct);
        if (membership.UnmanagedBaseline != null || membership.Contributions.Any(HasFacts))
        {
            return null;
        }
        var claim = facts
            .Inputs.SelectMany(input => input.Claims)
            .Single(claim => claim.Proposal.ProposalUuid == decision.ProposalUuid);
        var publication = new PeoplePublication(
            [new(creation.PersonUuid, creation.DisplayName, creation.Kind)],
            [
                new(
                    StableUuid(application.AttemptUuid, decision.ProposalUuid, "alias"),
                    creation.PersonUuid,
                    creation.DisplayName,
                    claim.Proposal.Mention.Evidence,
                    "validated_match"
                ),
            ],
            [],
            []
        );
        return Stage(
            application,
            facts,
            decisions,
            membership,
            creation.PersonUuid,
            publication,
            null,
            []
        );
    }

    private async Task<PeopleAcceptanceStage?> PrepareCreditStageAsync(
        PeopleAcceptanceApplication application,
        NormalizationPeopleResolutionPreparation facts,
        IReadOnlyList<PeopleAcceptanceClaimDecision> decisions,
        CancellationToken ct
    )
    {
        var membership = await work.GetInputContributionsAsync(
            new("people", "recording", application.SourceUuid),
            cancellationToken: ct
        );
        if (membership.UnmanagedBaseline != null)
        {
            return null;
        }
        var byProposal = decisions.ToDictionary(decision => decision.ProposalUuid);
        var coverage = await ReadRetirementCoverageAsync(
            application.SourceUuid,
            facts,
            byProposal,
            ct
        );
        var replacements = facts
            .Inputs.Select(input => new RecordingCreditContributorReplacement(
                input.OriginalInput.Contributor,
                coverage
                    .Where(scope =>
                        scope.Contributor == input.OriginalInput.Contributor && scope.Complete
                    )
                    .Select(scope => scope.Scope)
                    .ToArray(),
                input
                    .Claims.Where(claim =>
                        byProposal[claim.Proposal.ProposalUuid].Action
                        == PeopleResolutionAction.MatchExisting
                    )
                    .Select(claim => new RecordingCredit(
                        StableUuid(application.AttemptUuid, claim.Proposal.ProposalUuid, "credit"),
                        application.SourceUuid,
                        Role(claim.Proposal.Mention.Role),
                        claim.Proposal.Mention.Name,
                        byProposal[claim.Proposal.ProposalUuid].PersonUuid,
                        claim.ScopeTrackUuid,
                        [claim.Proposal.Mention.Evidence],
                        Basis: "validated_match",
                        ReasonCode: "evaluated_people_resolution"
                    ))
                    .ToArray()
            ))
            .ToArray();
        var hasAcceptedActions = replacements.Any(value =>
            value.Credits.Count != 0 || value.CompleteScopes.Count != 0
        );
        // An acknowledgment owns no accepted rows, so peer contributions may stay in the
        // recording owner unchanged. Reconciliation retains the stricter ownership check.
        if (hasAcceptedActions && membership.Contributions.Any(HasFacts))
        {
            var configured = await store.GetConfiguredStageAsync(
                application.SourceUuid,
                membership.Owner!.DesiredGeneration,
                ct
            );
            if (configured == null || !OwnsConfiguredBindings(membership, configured.Contributions))
            {
                return null;
            }
        }
        var selectedPeople = decisions
            .Where(decision => decision.Action == PeopleResolutionAction.MatchExisting)
            .Select(decision => decision.PersonUuid!.Value)
            .ToHashSet();
        var owners = facts
            .Inputs.SelectMany(input => input.Claims)
            .Where(claim => claim.Candidates != null)
            .SelectMany(claim =>
                claim.Candidates!.Facts.Select(candidate => new
                {
                    candidate.Hit.Uuid,
                    candidate.Header.OwnerSubjectUuid,
                })
            )
            .Where(candidate => selectedPeople.Contains(candidate.Uuid))
            .Select(candidate => candidate.OwnerSubjectUuid)
            .OfType<Guid>()
            .ToHashSet();
        var observations = facts
            .Inputs.SelectMany(input => input.Claims)
            .Where(claim => claim.Candidates != null)
            .SelectMany(claim => claim.Candidates!.ReadSet.OwnerObservations)
            .Where(observation => owners.Contains(observation.SubjectUuid))
            .Distinct()
            .ToArray();
        if (
            owners.Count != selectedPeople.Count
            || observations.Select(value => value.SubjectUuid).Distinct().Count() != owners.Count
        )
        {
            throw new NormalizationConflictException(
                "Each accepted person requires its observed registered identity owner."
            );
        }
        var stage = Stage(
            application,
            facts,
            decisions,
            membership,
            null,
            null,
            hasAcceptedActions
                ? new(
                    application.SourceUuid,
                    application.Plan!.Proposal.ExtractionAttemptUuid,
                    replacements
                )
                : null,
            observations
        ) with
        {
            ScopeCoverage = coverage,
            DecisionOnly = !hasAcceptedActions,
        };
        return stage.DecisionOnly ? await PrepareDecisionPeersAsync(stage, membership, ct) : stage;
    }

    private async Task<PeopleAcceptanceStage?> PrepareDecisionStageAsync(
        PeopleAcceptanceApplication application,
        CancellationToken ct
    )
    {
        var proposal = application.Plan!.Proposal;
        await RevalidateOriginalInputsAsync(proposal, ct);
        var membership = await work.GetInputContributionsAsync(
            new("people", "recording", application.SourceUuid),
            cancellationToken: ct
        );
        if (membership.UnmanagedBaseline != null)
        {
            return null;
        }
        var stage = Stage(
            application,
            proposal.Preparation,
            [],
            membership,
            null,
            null,
            null,
            []
        ) with
        {
            DecisionOnly = true,
        };
        return await PrepareDecisionPeersAsync(stage, membership, ct);
    }

    private static PeopleAcceptanceStage Stage(
        PeopleAcceptanceApplication application,
        NormalizationPeopleResolutionPreparation facts,
        IReadOnlyList<PeopleAcceptanceClaimDecision> decisions,
        NormalizationInputContributionsSnapshot membership,
        Guid? person,
        PeoplePublication? identity,
        RecordingCreditReconciliation? reconciliation,
        IReadOnlyList<NormalizationSubjectObservation> dependencies
    )
    {
        var original = application.Plan!.Proposal;
        var proposalStamp = new NormalizationSubjectObservation(
            original.Ticket.SubjectUuid,
            original.Ticket.DesiredGeneration,
            original.Ticket.EditorialRevision,
            original.AttemptUuid
        );
        return new(
            StableUuid(
                application.AttemptUuid,
                person ?? application.SourceUuid,
                person.HasValue ? "identity-work" : "credit-work"
            ),
            person,
            application.Plan.Policy,
            facts,
            decisions,
            PeopleAcceptanceReadSet.Contributions(
                facts,
                proposalStamp,
                application.Plan.PolicyState?.OwnerObservation
                    ?? application.Plan.Policy!.OwnerObservation,
                membership,
                dependencies
            ),
            membership.Owner?.DesiredGeneration,
            membership
                .Contributions.Select(value => new NormalizationInputContributionRevision(
                    value.Contributor,
                    value.Revision
                ))
                .ToArray(),
            identity,
            reconciliation,
            PolicyState: application.Plan.PolicyState
        );
    }

    private static string Role(RecordingCreditRole role) =>
        role switch
        {
            RecordingCreditRole.MatrixBuilder => "matrix_builder",
            RecordingCreditRole.MasteringEngineer => "mastering_engineer",
            RecordingCreditRole.RecordingEngineer => "recording_engineer",
            _ => role.ToString().ToLowerInvariant(),
        };

    private static bool OwnsConfiguredBindings(
        NormalizationInputContributionsSnapshot snapshot,
        IReadOnlyList<NormalizationPreparedInputContribution> configured
    )
    {
        var known = configured.ToDictionary(input => input.Contribution.Contributor);
        return snapshot
            .Contributions.Where(HasFacts)
            .All(state =>
                known.TryGetValue(state.Contributor, out var input)
                && state.Resources.ToHashSet().SetEquals(input.Contribution.Resources)
                && state
                    .DependencySubjectUuids.ToHashSet()
                    .SetEquals(input.Contribution.DependencySubjectUuids)
                && state
                    .CatalogArtistUuids.ToHashSet()
                    .SetEquals((input.CatalogInputs ?? []).Select(value => value.ArtistUuid))
                && state
                    .SourceIdentifiers.ToHashSet()
                    .SetEquals(
                        (input.SourceIdentities ?? []).Select(value => value.UpstreamIdentifier)
                    )
                && state.UsesMappingRegistry == (input.MappingRegistry != null)
                && (state.SourceUuids ?? [])
                    .ToHashSet()
                    .SetEquals((input.SourceUuids ?? []).Select(value => value.SourceUuid))
                && (state.OwnerObservationSubjectUuids ?? [])
                    .ToHashSet()
                    .SetEquals(input.Contribution.OwnerObservationSubjectUuids ?? [])
                && (state.CandidateUniverses ?? [])
                    .ToHashSet()
                    .SetEquals((input.CandidateUniverses ?? []).Select(value => value.Universe))
                && (state.ImportedVenueUuids?.Count ?? 0) == 0
            );
    }

    private static bool HasFacts(NormalizationInputContributionState input) =>
        input.Resources.Count
            + input.DependencySubjectUuids.Count
            + input.CatalogArtistUuids.Count
            + input.SourceIdentifiers.Count
            + (input.SourceUuids?.Count ?? 0)
            + (input.CandidateUniverses?.Count ?? 0)
            + (input.OwnerObservationSubjectUuids?.Count ?? 0)
            + (input.ImportedVenueUuids?.Count ?? 0)
            > 0
        || input.UsesMappingRegistry;
}
