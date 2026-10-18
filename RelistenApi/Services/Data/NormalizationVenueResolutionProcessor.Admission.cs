using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationVenueResolutionProcessor
{
    private async Task<NormalizationWorkTicket> AdmitStageAsync(
        VenueResolutionAttempt attempt,
        VenueResolutionStage stage,
        CancellationToken ct
    )
    {
        var creation = stage.Kind == VenueResolutionStageKind.CreateVenue;
        var owner =
            creation
                ? new NormalizationSubjectKey(
                    "venues",
                    "venue",
                    stage.Publication.Venues.Single().Uuid
                )
            : stage.Kind == VenueResolutionStageKind.SupersedeObservation
                ? VenueResolutionOwner.ForObservation(attempt.Preparation!.Source)
            : VenueResolutionOwner.For(attempt.Request, attempt.Preparation!.Source);
        var ownerUuid = owner.Uuid;
        var membership = stage.OwnerMembership;
        var contributor = new NormalizationResourceKey(
            "relisten",
            creation ? "venue_creation" : "venue_resolution",
            (creation ? attempt.AttemptUuid : ownerUuid).ToString("D")
        );
        var references = stage.OwnerInputReferences;
        var prepared = membership
            .Contributions.Where(state => state.Contributor != contributor)
            .Select(state => PrepareVenuePeer(state, stage.ReadSet, references))
            .ToList();
        if (membership.UnmanagedBaseline is { } baseline)
        {
            // Work adopts an unmanaged baseline at revision one before this explicit replacement;
            // the membership CAS still names the original virtual revision zero.
            prepared.Add(
                PrepareVenuePeer(baseline with { Revision = 1 }, stage.ReadSet, references)
            );
        }
        var own = creation ? stage.ReadSet : DecisionStageReadSet(attempt, stage);
        var revisions = own.Inputs.Select(input => input.RevisionUuid).ToHashSet();
        prepared.Add(
            PrepareVenueOwner(
                contributor,
                membership
                    .Contributions.SingleOrDefault(state => state.Contributor == contributor)
                    ?.Revision
                    ?? 0,
                own,
                references.Where(input => revisions.Contains(input.InputRevisionUuid)).ToArray()
            )
        );
        var expected = membership
            .Contributions.Select(state => new NormalizationInputContributionRevision(
                state.Contributor,
                state.Revision
            ))
            .Concat(
                membership.UnmanagedBaseline is { } unmanaged
                    ? [new NormalizationInputContributionRevision(unmanaged.Contributor, 0)]
                    : []
            )
            .ToArray();
        var ticket = await work.BeginContributedBatchAsync(
            stage.WorkUuid,
            owner,
            attempt.ProcessingVersion,
            prepared,
            membership.Owner?.DesiredGeneration ?? 0,
            ct,
            expected
        );
        NormalizationVenueResolutionStore.RequireTicket(stage, ticket, attempt.ProcessingVersion);
        return ticket;
    }

    private static VenueResolutionReadSet DecisionStageReadSet(
        VenueResolutionAttempt attempt,
        VenueResolutionStage stage
    )
    {
        if (stage.Kind == VenueResolutionStageKind.SupersedeObservation)
        {
            return EmptyVenueReadSet();
        }
        if (
            stage.Kind
            is VenueResolutionStageKind.AcknowledgeMissing
                or VenueResolutionStageKind.AcknowledgeDecision
        )
        {
            return attempt.Preparation!.ReadSet;
        }
        return AssignmentDecisionReadSet(attempt, stage.DerivedContext);
    }

    private static VenueResolutionReadSet AssignmentDecisionReadSet(
        VenueResolutionAttempt attempt,
        VenueResolutionContext? derivedContext
    )
    {
        var context = derivedContext ?? attempt.Preparation!.Context;
        var read =
            derivedContext == null
                ? attempt.Decision!.ReadSet
                : SourceDecisionReadSet(
                    attempt.Preparation!.Source,
                    context,
                    attempt.Preparation.Policy,
                    attempt.Decision!.ReadSet.Inputs
                );
        var target = context
            .Candidates.Facts.Concat(context.AssignmentTargets)
            .DistinctBy(fact => fact.Header.Uuid)
            .Single(fact => fact.Header.Uuid == attempt.Decision!.TargetVenueUuid);
        var observation = context.ReadSet.OwnerObservations.Single(owner =>
            owner.SubjectUuid == target.Header.OwnerSubjectUuid
        );
        return MergeVenueReadSets([
            read,
            new(
                [],
                [
                    new(
                        observation.SubjectUuid,
                        observation.DesiredGeneration,
                        observation.EditorialRevision,
                        observation.PublishedWorkUuid
                    ),
                ],
                [],
                [],
                null,
                [],
                [],
                [],
                []
            ),
        ]);
    }

    private static VenueResolutionReadSet SourceDecisionReadSet(
        VenueResolutionSource source,
        VenueResolutionContext context,
        ActiveVenueAcceptancePolicy? policy,
        IReadOnlyList<NormalizationInputDependency> importedInputs
    )
    {
        var original = source.OriginalTicket;
        var read =
            original == null
                ? new VenueResolutionReadSet(
                    importedInputs,
                    [],
                    source.ImportedBinding?.CatalogInputs ?? [],
                    [],
                    null,
                    source.ImportedBinding?.ImportedVenues
                        ?? (
                            source.Absence is { } absent
                                ?
                                [
                                    new NormalizationImportedVenueDependency(
                                        absent.ImportedVenueUuid,
                                        absent.Generation
                                    ),
                                ]
                                : []
                        ),
                    [],
                    [],
                    []
                )
                : new(
                    original.Inputs,
                    original.Dependencies,
                    original.CatalogInputs ?? [],
                    original.SourceIdentities ?? [],
                    original.MappingRegistry,
                    original.ImportedVenues ?? [],
                    original.CandidateUniverses ?? [],
                    original.OwnerObservations ?? [],
                    original.SourceUuids ?? []
                );
        IEnumerable<NormalizationSubjectObservation> factsOwners = source.Absence
            is { Exists: false }
            ? []
            : context.ReadSet.OwnerObservations;
        return MergeVenueReadSets([
            read,
            new(
                [],
                [],
                context.ReadSet.CatalogInputs,
                [],
                null,
                [],
                context.ReadSet.CandidateUniverses,
                factsOwners
                    .Concat(source.ProposalOwner is { } proposal ? [proposal] : [])
                    .Concat(policy is { } active ? [active.OwnerObservation] : [])
                    .ToArray(),
                []
            ),
        ]);
    }
}
