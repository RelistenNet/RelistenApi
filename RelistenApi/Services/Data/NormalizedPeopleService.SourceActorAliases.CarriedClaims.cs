using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizedPeopleService
{
    private static async Task<
        IReadOnlyList<SourceActorAliasPlannedClaim>
    > AuthenticateCarriedSourceAliasClaims(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SourceActorAliasApplication application,
        SourceActorAliasPersonStage stage,
        SourceActorResolutionReviewSnapshot reviews,
        bool sourceCurrent,
        CancellationToken ct
    )
    {
        var references = stage.Facts.CarriedClaims ?? [];
        if (references.Count == 0)
        {
            return [];
        }
        if (
            !sourceCurrent
            || references.Select(value => value.Claim).Distinct().Count() != references.Count
        )
        {
            throw new ArgumentException(
                "Committed claim references require the current original source and unique complete claim coordinates."
            );
        }

        // Certificates retain the original acceptance, not a new matching decision. The
        // reader authenticates their original published request; current source, review,
        // target and active-support checks below still belong to this transaction.
        var certificates = await SourceActorAliasApplicationStore.RequirePublishedStageHeaders(
            c,
            t,
            application.AttemptUuid,
            references.Select(value => value.StageUuid).Distinct().ToArray(),
            ct
        );
        var claims = new SourceActorAliasApplicationIndex(certificates.Values);
        var publishedStages = application
            .Stages.Where(value => value.Receipt != null)
            .Select(value => value.StageUuid)
            .ToHashSet();
        var targets = stage.Facts.Targets.Select(value => value.OriginalPersonUuid).ToHashSet();
        var supportIds = certificates
            .Values.SelectMany(value => value.Receipt!.Names)
            .Where(value => value.SupportUuid.HasValue)
            .Select(value => value.SupportUuid!.Value)
            .Distinct()
            .ToArray();
        var activeSupports = (
            await c.QueryAsync<Guid>(
                new CommandDefinition(
                    """
                    SELECT support.uuid FROM normalization.person_alias_contributor_support support
                    JOIN normalization.source_actor_alias_assignments assignment ON assignment.uuid=support.assignment_uuid
                    WHERE support.uuid=ANY(@supportIds) AND support.retired_work_uuid IS NULL AND assignment.retired_work_uuid IS NULL
                    """,
                    new { supportIds },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToHashSet();

        var result = new List<SourceActorAliasPlannedClaim>();
        foreach (var reference in references)
        {
            var key = reference.Claim;
            if (
                key.Proposal != application.Plan!.Proposal
                || key.InputIndex < 0
                || key.InputIndex >= stage.Facts.Inputs.Count
                || key.ScopeIndex < 0
                || key.ScopeIndex >= stage.Facts.Inputs[key.InputIndex].Scopes.Count
            )
            {
                throw new ArgumentException(
                    "A carried claim must identify an actual original source scope."
                );
            }
            var input = stage.Facts.Inputs[key.InputIndex];
            var scope = input.Scopes[key.ScopeIndex];
            if (
                key.AssertionIndex < 0
                || key.AssertionIndex >= scope.OriginalScope.Extraction.Assertions.Count
            )
            {
                throw new ArgumentException(
                    "A carried claim must identify an actual original source assertion."
                );
            }
            if (
                !claims.Claims.TryGetValue(key, out var committed)
                || committed.Stage.StageUuid != reference.StageUuid
                || reference.StageUuid == stage.StageUuid
                || !publishedStages.Contains(reference.StageUuid)
            )
            {
                throw new ArgumentException(
                    "A carried claim requires an earlier committed receipt in this exact application."
                );
            }
            var assertion = scope.OriginalScope.Extraction.Assertions[key.AssertionIndex];
            var coordinates = new SourceActorAliasCoordinates(
                application.SourceUuid,
                input.OriginalInput.Contributor,
                stage
                    .Facts.OriginalExtractionTicket.Inputs.Single(value =>
                        value.RevisionUuid == scope.OriginalScope.InputRevisionUuid
                    )
                    .ResourceUuid,
                scope.OriginalScope.InputRevisionUuid,
                assertion.NameEvidence.JsonPointer,
                scope.OriginalScope.ProducerVersion,
                assertion.AssertionUuid,
                assertion.ActorReferenceUuid,
                assertion.FieldSha256
            );
            var outcomes = committed.Outcomes;
            if (
                reference.OriginalPersonUuid != committed.Stage.OriginalPersonUuid
                || committed.Acceptance.OriginalSelectedPersonUuid != reference.OriginalPersonUuid
                || !targets.Contains(reference.OriginalPersonUuid)
                || !SourceAliasSame(
                    committed.PreparedNames,
                    SourceActorAliasApplicationService.Names(
                        committed.Stage.StageUuid,
                        key,
                        assertion
                    )
                )
                || outcomes.Count != committed.PreparedNames.Count
                || !outcomes
                    .Select(value => value.RequestedAliasUuid)
                    .ToHashSet()
                    .SetEquals(committed.PreparedNames.Select(value => value.RequestedAliasUuid))
                || outcomes.Any(value =>
                    committed
                        .PreparedNames.Single(name =>
                            name.RequestedAliasUuid == value.RequestedAliasUuid
                        )
                        .Wording != value.Wording
                )
                || !SourceActorAliasApplicationService.CanCarrySourceClaim(
                    committed.Acceptance,
                    coordinates,
                    reviews
                )
            )
            {
                throw new NormalizationConflictException(
                    "The committed claim receipt no longer supplies the exact original source selection and current review authority."
                );
            }
            if (
                outcomes.Any(value =>
                    value.SupportUuid.HasValue && !activeSupports.Contains(value.SupportUuid.Value)
                )
            )
            {
                throw new NormalizationConflictException(
                    "A carried committed claim's actual source support was retired."
                );
            }
            result.Add(
                new(
                    key,
                    SourceActorAliasApplicationDisposition.Complete,
                    "committed_source_claim_receipt",
                    reference.OriginalPersonUuid
                )
            );
        }
        return result;
    }
}
