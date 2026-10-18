using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizedPeopleService
{
    private static async Task ValidateSourceAliasAddition(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SourceActorAliasApplication application,
        SourceActorAliasPersonStage stage,
        SourceActorAliasStageDelta delta,
        CancellationToken ct
    )
    {
        var authority =
            delta.Acceptance
            ?? throw new ArgumentException(
                "Source addition requires its original acceptance proof."
            );
        if (
            authority.Claim.Proposal != application.Plan!.Proposal
            || authority.AcceptedStageUuid != null
            || delta.RetireAssignmentUuids.Count != 0
            || delta.RetireSupportUuids.Count != 0
            || delta.Observation != null
            || delta.Readiness != null
        )
        {
            throw new ArgumentException(
                "A new source assertion cannot carry unrelated retirement or replacement authority."
            );
        }
        var input = stage.Facts.Inputs[authority.Claim.InputIndex];
        var scope = input.Scopes[authority.Claim.ScopeIndex];
        var claim = scope.Claims.Single(value =>
            value.AssertionIndex == authority.Claim.AssertionIndex
        );
        var assertion = scope.OriginalScope.Extraction.Assertions[authority.Claim.AssertionIndex];
        if (
            delta.Scope
                != SourceActorAliasApplicationService.ScopeKey(
                    application.SourceUuid,
                    input,
                    scope,
                    claim.Coordinates.FieldJsonPointer
                )
            || delta.Coordinates != claim.Coordinates
            || delta.AssertionSha256 != claim.AssertionSha256
            || !SourceAliasSame(
                delta.Names,
                SourceActorAliasApplicationService.Names(
                    stage.StageUuid,
                    authority.Claim,
                    assertion
                )
            )
            || claim.Decision.PersonUuid != stage.OriginalPersonUuid
            || !stage.Facts.Targets.Any(target =>
                target.OriginalPersonUuid == stage.OriginalPersonUuid
            )
            || authority.OriginalSelectedPersonUuid != stage.OriginalPersonUuid
        )
        {
            throw new ArgumentException(
                "Source additions must retain the exact selected original person, scope, assertion and every explicit cited name."
            );
        }
        if (authority.Kind == SourceActorAliasAuthorityKind.Machine)
        {
            if (
                claim.Decision.Action != SourceActorResolutionAction.MatchExisting
                || stage.Facts.Policy == null
                || authority.ReviewPreparationUuid != null
                || authority.ReviewStateUuid != null
                || authority.ReviewRevision != 0
                || authority.ReviewOperationUuid != null
            )
            {
                throw new ArgumentException(
                    "Machine aliases require a fresh qualified acceptance without invented human authority."
                );
            }
            return;
        }
        if (
            authority.Kind
                is not (
                    SourceActorAliasAuthorityKind.Reviewed
                    or SourceActorAliasAuthorityKind.Correspondence
                )
            || claim.Decision.Action != SourceActorResolutionAction.Review
            || claim.Decision.Reason != "reviewed_source_acceptance"
        )
        {
            throw new ArgumentException(
                "Trusted alias selection requires an actual supported source review."
            );
        }
        var review =
            stage.Facts.SourceReviews.Reviews.SingleOrDefault(value =>
                value.Uuid == authority.ReviewStateUuid
            )
            ?? throw new ArgumentException(
                "Trusted source authority is absent from its exact current review inventory."
            );
        var human = await RequireSourceAliasHuman(c, t, review, authority, ct);
        if (review.State != "accepted" || review.SelectedPersonUuid != stage.OriginalPersonUuid)
        {
            throw new NormalizationConflictException(
                "The original source review no longer accepts its selected original UUID."
            );
        }
        if (authority.Kind == SourceActorAliasAuthorityKind.Correspondence)
        {
            if (
                !(stage.Facts.SourceReviews.Correspondences ?? []).Any(value =>
                    value.Current.Coordinates == claim.Coordinates
                    && value.OriginalReview.Uuid == review.Uuid
                    && value.OriginalReview.ReviewRevision == review.ReviewRevision
                    && value.OriginalReview.LastOperationUuid == review.LastOperationUuid
                    && value.Target.OriginalPersonUuid == stage.OriginalPersonUuid
                )
            )
            {
                throw new ArgumentException(
                    "Carried review selection requires the authenticated whole-field correspondence and target proof."
                );
            }
        }
        else
        {
            var field = SourceActorReviewEvidence.SelectField(
                await SourceActorResolutionStore.ReadPublishedExtractionAsync(
                    c,
                    t,
                    stage.Facts.ExtractionAttemptUuid,
                    application.SourceUuid,
                    ct
                ),
                stage.Facts.Inputs,
                claim.Coordinates
            );
            if (
                review.Coordinates != claim.Coordinates
                || !SourceActorReviewEvidence.Same(
                    field with
                    {
                        ExtractionAttemptUuid = human.OriginalField.ExtractionAttemptUuid,
                    },
                    human.OriginalField
                )
            )
            {
                throw new ArgumentException(
                    "Exact human source selection must retain its complete original field."
                );
            }
        }
    }

    private static async Task<SourceActorReviewPreparation> RequireSourceAliasHuman(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SourceActorAliasReviewState review,
        SourceActorAliasAcceptanceReference authority,
        CancellationToken ct
    )
    {
        var human =
            await SourceActorReviewedEvidence.ReadReviewPreparationAsync(c, t, review, ct)
            ?? throw new NormalizationConflictException(
                "Source authority lacks its supported immutable human operation."
            );
        if (
            authority.ReviewPreparationUuid != human.PreparationUuid
            || authority.ReviewStateUuid != review.Uuid
            || authority.ReviewRevision != review.ReviewRevision
            || authority.ReviewOperationUuid != review.LastOperationUuid
            || authority.OriginalSelectedPersonUuid != review.SelectedPersonUuid
        )
        {
            throw new ArgumentException(
                "Trusted source authority must retain the original review CAS and operation without restamping it."
            );
        }
        var extraction = await SourceActorResolutionStore.ReadPublishedExtractionAsync(
            c,
            t,
            human.Request.ExtractionAttemptUuid,
            review.Coordinates.SourceUuid,
            ct
        );
        var inputs = await SourceActorResolutionStore.ReadOriginalInputsAsync(c, t, extraction, ct);
        if (
            !SourceActorReviewEvidence.Same(
                SourceActorReviewEvidence.SelectField(extraction, inputs, review.Coordinates),
                human.OriginalField
            )
        )
        {
            throw new ArgumentException(
                "The original human field differs from the actual retained published extraction."
            );
        }
        return human;
    }

    private static async Task ValidateSourceAliasWithdrawal(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SourceActorAliasPersonStage stage,
        SourceActorAliasStageDelta delta,
        CancellationToken ct
    )
    {
        var authority =
            delta.Acceptance
            ?? throw new ArgumentException("Source withdrawal requires its exact human receipt.");
        var review =
            stage.Facts.SourceReviews.Reviews.SingleOrDefault(value =>
                value.Uuid == authority.ReviewStateUuid
            )
            ?? throw new NormalizationConflictException(
                "The original source withdrawal review is no longer current."
            );
        var original = await SourceActorAliasApplicationService.ReadOriginalClaim(
            c,
            t,
            authority.Claim,
            ct
        );
        var entry = stage.PriorMap.Single(value => value.Scope == delta.Scope);
        var groups = entry
            .Bindings.Where(binding => delta.RetireAssignmentUuids.Contains(binding.AssignmentUuid))
            .ToArray();
        if (
            authority.Kind != SourceActorAliasAuthorityKind.Withdrawal
            || original.Claim.Coordinates != review.Coordinates
                && !groups.Any(binding => binding.Acceptance.ReviewStateUuid == review.Uuid)
            || delta.Coordinates != review.Coordinates
            || delta.AssertionSha256 != review.AssertionSha256
            || review.State == "accepted" && review.SelectedPersonUuid == stage.OriginalPersonUuid
            || review.State is not ("accepted" or "rejected" or "retracted")
            || delta.Names.Count != 0
            || delta.Readiness != null
            || delta.Observation != null
            || groups.Length == 0
            || !groups
                .Select(binding => binding.AssignmentUuid)
                .ToHashSet()
                .SetEquals(delta.RetireAssignmentUuids)
            || !groups
                .Select(binding => binding.SupportUuid)
                .ToHashSet()
                .SetEquals(delta.RetireSupportUuids)
        )
        {
            throw new ArgumentException(
                "Withdrawal may retire only the exact original binding disproved by its current trusted review."
            );
        }
        await RequireSourceAliasHuman(c, t, review, authority, ct);
    }

    private static async Task ValidateSourceAliasFinalization(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SourceActorAliasApplication application,
        SourceActorAliasPersonStage stage,
        SourceActorAliasApplicationService.PreparedSource source,
        SourceActorAliasStageDelta delta,
        CancellationToken ct
    )
    {
        var readiness =
            delta.Readiness
            ?? throw new ArgumentException(
                "Replacement retirement requires its durable readiness proof."
            );
        var entry = stage.PriorMap.Single(value => value.Scope == delta.Scope);
        var expected = (
            await SourceActorAliasApplicationService.BuildReadiness(c, t, application, source, ct)
        ).SingleOrDefault(value => value.CompleteObservation == readiness.CompleteObservation);
        if (
            expected == null
            || !SourceAliasSame(
                readiness,
                expected with
                {
                    PredecessorStageUuid = stage.PredecessorStageUuid,
                }
            )
            || delta.Observation != readiness.CompleteObservation
            || !readiness.CompleteObservation.Complete
            || delta.Kind == SourceActorAliasDeltaKind.Clear
                != readiness.CompleteObservation.ExplicitClear
            || delta.Names.Count != 0
            || delta.Acceptance != null
            || delta.Coordinates != null
            || delta.AssertionSha256 != null
        )
        {
            throw new ArgumentException(
                "Incomplete, absent or blocked source fields cannot finalize replacement or clear accepted support."
            );
        }
        var observation = readiness.CompleteObservation;
        var input = stage.Facts.Inputs[observation.InputIndex];
        var scope = input.Scopes[
            observation.ScopeIndex
                ?? throw new ArgumentException(
                    "Complete coverage requires an actual original scope."
                )
        ];
        if (
            observation.Proposal != application.Plan!.Proposal
            || delta.Scope
                != SourceActorAliasApplicationService.ScopeKey(
                    application.SourceUuid,
                    input,
                    scope,
                    observation.FieldJsonPointer
                )
        )
        {
            throw new ArgumentException(
                "Complete field proof belongs to another exact source scope."
            );
        }
        await ValidateSourceAliasRequiredNames(c, t, application, readiness.RequiredNames, ct);
        var keep = readiness.RequiredNames.Select(value => value.SupportUuid).ToHashSet();
        var retire = entry.Bindings.Where(binding => !keep.Contains(binding.SupportUuid)).ToArray();
        var assignments = retire
            .Select(binding => binding.AssignmentUuid)
            .Distinct()
            .Where(uuid =>
                !entry.Bindings.Any(binding =>
                    binding.AssignmentUuid == uuid && keep.Contains(binding.SupportUuid)
                )
            )
            .ToArray();
        if (
            !retire
                .Select(binding => binding.SupportUuid)
                .ToHashSet()
                .SetEquals(delta.RetireSupportUuids)
            || !assignments.ToHashSet().SetEquals(delta.RetireAssignmentUuids)
        )
        {
            throw new ArgumentException(
                "Replacement may retire only superseded exact prepared support IDs."
            );
        }
    }

    private static async Task ValidateSourceAliasRequiredNames(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SourceActorAliasApplication application,
        IReadOnlyList<SourceActorAliasRequiredNameReceipt> requiredNames,
        CancellationToken ct
    )
    {
        var supportIds = requiredNames.Select(value => value.SupportUuid).Distinct().ToArray();
        var active = (
            await c.QueryAsync<SourceAliasRequiredNameRow>(
                new CommandDefinition(
                    """
                    SELECT support.uuid AS "SupportUuid",support.alias_uuid AS "AliasUuid",assignment.uuid AS "AssignmentUuid",
                        alias.wording AS "Wording",alias.person_uuid AS "PersonUuid"
                    FROM normalization.person_alias_contributor_support support
                    JOIN normalization.source_actor_alias_assignments assignment ON assignment.uuid=support.assignment_uuid
                    JOIN normalization.current_person_aliases alias ON alias.uuid=support.alias_uuid
                    WHERE support.uuid=ANY(@supportIds) AND support.retired_work_uuid IS NULL AND assignment.retired_work_uuid IS NULL
                    """,
                    new { supportIds },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToHashSet();
        var certificates = await SourceActorAliasApplicationStore.RequirePublishedStageHeaders(
            c,
            t,
            application.AttemptUuid,
            requiredNames.Select(value => value.StageUuid).Distinct().ToArray(),
            ct
        );
        foreach (var group in requiredNames.GroupBy(value => value.StageUuid))
        {
            var committed = certificates[group.Key];
            var outcomes = (committed.Receipt?.Names ?? [])
                .Where(name =>
                    name.Disposition
                        is SourceActorAliasNameDisposition.Accepted
                            or SourceActorAliasNameDisposition.ExactDuplicate
                )
                .Select(name =>
                    (
                        name.Claim,
                        name.Wording,
                        name.ActualAliasUuid,
                        name.AssignmentUuid,
                        name.SupportUuid
                    )
                )
                .ToHashSet();
            foreach (var required in group)
            {
                if (
                    !outcomes.Contains(
                        (
                            required.Claim,
                            required.Wording,
                            required.AliasUuid,
                            required.AssignmentUuid,
                            required.SupportUuid
                        )
                    )
                    || !active.Contains(
                        new(
                            required.SupportUuid,
                            required.AliasUuid,
                            required.AssignmentUuid,
                            required.Wording,
                            committed.OriginalPersonUuid
                        )
                    )
                )
                {
                    throw new NormalizationConflictException(
                        "Required replacement name no longer has its actual committed successful support receipt."
                    );
                }
            }
        }
    }

    private sealed record SourceAliasRequiredNameRow(
        Guid SupportUuid,
        Guid AliasUuid,
        Guid AssignmentUuid,
        string Wording,
        Guid PersonUuid
    );
}
