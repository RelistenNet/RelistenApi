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

public sealed partial class SourceActorAliasApplicationService
{
    private async Task<NextStage> PreparePersonStage(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SourceActorAliasApplication application,
        PreparedSource source,
        Guid person,
        PersonMap map,
        IReadOnlyList<SourceActorAliasStageDelta> deltas,
        IReadOnlyList<SourceActorAliasReplacementReadiness> readiness,
        CancellationToken ct
    )
    {
        source = await IncludeHistoricalReviews(c, t, source, map.Map, application.SourceUuid, ct);
        var target = await SourceActorReviewTargets.ReadAsync(c, t, person, ct);
        var targets = source
            .Targets.Where(value => value.OriginalPersonUuid != person)
            .Append(target)
            .ToArray();
        var read = await ReadStageReadSet(c, t, source, map.Map, targets, map.Membership, ct);
        var contributions = await Contributions(c, t, map, read, map.Map, source, ct);
        var generation = map.Membership.Owner!.DesiredGeneration;
        var ordinal = application.Stages.Count;
        var stageUuid = StableUuid(application.AttemptUuid, "stage/" + ordinal);
        var stage = new SourceActorAliasPersonStage(
            stageUuid,
            StableUuid(application.AttemptUuid, "work/" + ordinal),
            person,
            map.Membership.Owner.SubjectUuid,
            map.Committed?.StageUuid,
            map.Configured?.StageUuid,
            generation,
            map.Membership.Contributions.Select(value => new NormalizationInputContributionRevision(
                    value.Contributor,
                    value.Revision
                ))
                .Concat(
                    map.Membership.UnmanagedBaseline is { } baseline
                        ? new[]
                        {
                            new NormalizationInputContributionRevision(baseline.Contributor, 0),
                        }
                        : []
                )
                .ToArray(),
            contributions,
            map.Map,
            deltas
                .Select(delta =>
                    delta.Readiness == null
                        ? delta
                        : delta with
                        {
                            Readiness = delta.Readiness with
                            {
                                PredecessorStageUuid = map.Committed?.StageUuid,
                            },
                        }
                )
                .ToArray(),
            new(
                source.Extraction.AttemptUuid,
                source.Extraction.Ticket,
                source.Inputs,
                source.Reviews,
                source.Pools,
                source.Policy,
                targets,
                read.OwnerObservations ?? [],
                map.Membership,
                read,
                source.CarriedClaims.Count == 0 ? null : source.CarriedClaims
            )
        );
        // Exact receipt shape is bounded before admission. Fixed UUIDs below stand for every
        // possible accepted name, so no actual output can evade the independent final guard.
        var conservativeNames = deltas
            .Where(delta => delta.Acceptance != null)
            .SelectMany(delta =>
                delta.Names.Select(name => new SourceActorAliasNameOutcome(
                    delta.Acceptance!.Claim,
                    name.RequestedAliasUuid,
                    name.Wording,
                    SourceActorAliasNameDisposition.ProtectedReviewedNaturalClaim,
                    "canonical_alias_review_preserved_previous_support",
                    name.RequestedAliasUuid,
                    name.RequestedAliasUuid,
                    name.RequestedAliasUuid
                ))
            )
            .ToArray();
        var conservativeMap = map.Map.ToList();
        foreach (var delta in deltas)
        {
            switch (delta.Kind)
            {
                case SourceActorAliasDeltaKind.Add:
                    NormalizedPeopleService.ApplySourceAliasAddition(
                        conservativeMap,
                        stage,
                        delta,
                        delta
                            .Names.Select(name => new SourceActorAliasMapBinding(
                                name.RequestedAliasUuid,
                                name.RequestedAliasUuid,
                                name.RequestedAliasUuid,
                                delta.Acceptance! with
                                {
                                    AcceptedStageUuid = stage.StageUuid,
                                }
                            ))
                            .ToArray()
                    );
                    break;
                case SourceActorAliasDeltaKind.ObserveIncomplete:
                    NormalizedPeopleService.ApplySourceAliasIncompleteObservation(
                        conservativeMap,
                        delta
                    );
                    break;
                default:
                    NormalizedPeopleService.ApplySourceAliasRetirement(conservativeMap, delta);
                    break;
            }
        }
        SourceActorAliasApplicationCheckpointCodec.RequireReceiptCapacity(
            new(
                stage.StageUuid,
                stage.WorkUuid,
                person,
                conservativeNames,
                deltas.SelectMany(delta => delta.RetireAssignmentUuids).Distinct().ToArray(),
                deltas.SelectMany(delta => delta.RetireSupportUuids).Distinct().ToArray(),
                conservativeMap,
                SourceActorAliasApplicationDisposition.Partial,
                "canonical_alias_outcomes_preserved_previous_scope_support"
            )
        );
        return new(
            stage,
            SourceActorAliasApplicationDisposition.Partial,
            "source_person_stage_prepared",
            readiness
        );
    }

    internal static async Task<IReadOnlyList<SourceActorAliasReplacementReadiness>> BuildReadiness(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SourceActorAliasApplication application,
        PreparedSource source,
        CancellationToken ct
    )
    {
        var result = new List<SourceActorAliasReplacementReadiness>();
        if (!source.Current)
        {
            return result;
        }
        var committedClaims = new SourceActorAliasApplicationIndex(application.Stages);
        var aliasIds = committedClaims
            .Claims.Values.SelectMany(claim => claim.Outcomes)
            .Where(name => name.ActualAliasUuid.HasValue)
            .Select(name => name.ActualAliasUuid!.Value)
            .Distinct()
            .ToArray();
        var currentAliases = await ReadCurrentSourceAliasNames(c, t, aliasIds, ct);
        for (var inputIndex = 0; inputIndex < source.Inputs.Count; inputIndex++)
        {
            var input = source.Inputs[inputIndex];
            if (
                !input.AliasPayloadPresent
                || input.DecodeIssues.Any(RecordingActorDecodeIssues.AffectsActorEvidence)
            )
            {
                continue;
            }
            for (var scopeIndex = 0; scopeIndex < input.Scopes.Count; scopeIndex++)
            {
                var scope = input.Scopes[scopeIndex];
                foreach (var field in scope.Fields)
                {
                    var coverage = scope.OriginalScope.Extraction.Scopes.SingleOrDefault(value =>
                        value.Field == field.Field
                    );
                    if (coverage?.Complete != true || coverage.Evidence.Count == 0)
                    {
                        continue;
                    }
                    var claims = source
                        .Claims.Where(value =>
                            value.Claim.InputIndex == inputIndex
                            && value.Claim.ScopeIndex == scopeIndex
                            && Coordinates(source, value.Claim).FieldJsonPointer
                                == field.Evidence.JsonPointer
                        )
                        .ToArray();
                    var required = new List<SourceActorAliasRequiredNameReceipt>();
                    var authorities = new List<SourceActorAliasAcceptanceReference>();
                    var complete = true;
                    foreach (var claim in claims)
                    {
                        var original = scope.Claims.SingleOrDefault(value =>
                            value.AssertionIndex == claim.Claim.AssertionIndex
                        );
                        if (
                            original?.Decision.Action
                            is SourceActorResolutionAction.Rejected
                                or SourceActorResolutionAction.Retracted
                        )
                        {
                            continue;
                        }
                        if (!committedClaims.Claims.TryGetValue(claim.Claim, out var committed))
                        {
                            complete = false;
                            break;
                        }
                        var outcomes = committed.Outcomes;
                        var count = Names(
                            Guid.Empty,
                            claim.Claim,
                            scope.OriginalScope.Extraction.Assertions[claim.Claim.AssertionIndex]
                        ).Count;
                        if (
                            !claim.OriginalPersonUuid.HasValue
                            || outcomes.Count != count
                            || committed.Stage.OriginalPersonUuid != claim.OriginalPersonUuid
                            || outcomes.Any(value =>
                                value.Disposition
                                    is not (
                                        SourceActorAliasNameDisposition.Accepted
                                        or SourceActorAliasNameDisposition.ExactDuplicate
                                    )
                                || value.ActualAliasUuid == null
                                || !currentAliases.TryGetValue(
                                    value.ActualAliasUuid.Value,
                                    out var currentName
                                )
                                || !currentName.Matches(
                                    committed.Stage.OriginalPersonUuid,
                                    committed.NamesByRequestedUuid[value.RequestedAliasUuid]
                                )
                                || value.AssignmentUuid == null
                                || value.SupportUuid == null
                            )
                        )
                        {
                            complete = false;
                            break;
                        }
                        required.AddRange(
                            outcomes.Select(value => new SourceActorAliasRequiredNameReceipt(
                                claim.Claim,
                                value.Wording,
                                committed.Stage.StageUuid,
                                value.ActualAliasUuid!.Value,
                                value.AssignmentUuid!.Value,
                                value.SupportUuid!.Value
                            ))
                        );
                        authorities.Add(committed.Acceptance);
                    }
                    if (complete)
                    {
                        result.Add(
                            new(
                                new(
                                    application.Plan!.Proposal,
                                    inputIndex,
                                    scopeIndex,
                                    field.Evidence.JsonPointer,
                                    true,
                                    claims.Length == 0
                                ),
                                required,
                                null,
                                authorities.Distinct().ToArray()
                            )
                        );
                    }
                }
            }
        }
        return result;
    }

    internal static async Task<
        IReadOnlyDictionary<Guid, CurrentSourceAliasName>
    > ReadCurrentSourceAliasNames(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        IReadOnlyList<Guid> aliasIds,
        CancellationToken ct
    ) =>
        (
            await c.QueryAsync<CurrentSourceAliasName>(
                new CommandDefinition(
                    """
                    SELECT uuid AS "Uuid",person_uuid AS "PersonUuid",wording AS "Wording",input_revision_uuid AS "InputRevisionUuid",
                        json_pointer AS "JsonPointer",start_code_point AS "StartCodePoint",end_code_point AS "EndCodePoint",quote AS "Quote"
                    FROM normalization.current_person_aliases WHERE uuid=ANY(@aliasIds)
                    """,
                    new { aliasIds },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToDictionary(value => value.Uuid);

    internal sealed record CurrentSourceAliasName(
        Guid Uuid,
        Guid PersonUuid,
        string Wording,
        Guid InputRevisionUuid,
        string JsonPointer,
        int? StartCodePoint,
        int? EndCodePoint,
        string? Quote
    )
    {
        internal bool Matches(Guid person, SourceActorAliasPreparedName name) =>
            PersonUuid == person
            && Wording == name.Wording
            && name.Evidence
                == new NormalizedEvidenceReference(
                    InputRevisionUuid,
                    JsonPointer,
                    StartCodePoint,
                    EndCodePoint,
                    Quote
                );
    }

    private static IReadOnlyList<SourceActorAliasStageDelta> Finalizations(
        SourceActorAliasApplication application,
        PreparedSource source,
        Guid person,
        IReadOnlyList<SourceActorAliasScopeMapEntry> map,
        IReadOnlyList<SourceActorAliasReplacementReadiness> readiness
    )
    {
        var result = new List<SourceActorAliasStageDelta>();
        foreach (var entry in map.Where(entry => entry.Scope.SourceUuid == application.SourceUuid))
        {
            var inputIndex = source
                .Inputs.ToList()
                .FindIndex(input => input.OriginalInput.Contributor == entry.Scope.Contributor);
            if (inputIndex < 0)
            {
                continue;
            }
            var input = source.Inputs[inputIndex];
            var scopeIndex = input
                .Scopes.ToList()
                .FindIndex(scope =>
                    ScopeKey(application.SourceUuid, input, scope, entry.Scope.FieldJsonPointer)
                    == entry.Scope
                );
            if (scopeIndex < 0)
            {
                continue;
            }
            var scope = input.Scopes[scopeIndex];
            var proof = readiness.SingleOrDefault(value =>
                value.CompleteObservation.InputIndex == inputIndex
                && value.CompleteObservation.ScopeIndex == scopeIndex
                && value.CompleteObservation.FieldJsonPointer == entry.Scope.FieldJsonPointer
            );
            if (proof == null)
            {
                continue;
            }
            var observation = proof.CompleteObservation;
            if (
                entry.LastCompleteObservation == observation
                && entry.IncompleteObservations.Count == 0
            )
            {
                continue;
            }
            var keepSupport = proof.RequiredNames.Select(value => value.SupportUuid).ToHashSet();
            var retired = entry
                .Bindings.Where(binding => !keepSupport.Contains(binding.SupportUuid))
                .ToArray();
            var assignmentIds = retired
                .Select(binding => binding.AssignmentUuid)
                .Distinct()
                .Where(uuid =>
                    !entry.Bindings.Any(binding =>
                        binding.AssignmentUuid == uuid && keepSupport.Contains(binding.SupportUuid)
                    )
                )
                .ToArray();
            result.Add(
                new(
                    observation.ExplicitClear
                        ? SourceActorAliasDeltaKind.Clear
                        : SourceActorAliasDeltaKind.FinalizeReplacement,
                    entry.Scope,
                    person,
                    null,
                    null,
                    null,
                    [],
                    assignmentIds,
                    retired.Select(binding => binding.SupportUuid).ToArray(),
                    observation,
                    proof
                )
            );
        }
        return result;
    }
}
