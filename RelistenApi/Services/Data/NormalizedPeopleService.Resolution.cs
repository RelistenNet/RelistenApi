using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizedPeopleService
{
    /// <summary>
    /// Publishes one evaluated identity stage from the application checkpoint. Work fences
    /// its original evidence, candidate observations and active policy before this callback.
    /// Callers cannot supply identity changes or matching authority through this entrypoint.
    /// </summary>
    public async Task<NormalizationPublicationResult> PublishResolutionIdentityAsync(
        Guid workUuid,
        Guid applicationAttemptUuid,
        Guid personUuid,
        CancellationToken cancellationToken = default
    )
    {
        var applications = new PeopleAcceptanceApplicationStore(db);
        await RequireResolutionStageRequest(
            applications,
            applicationAttemptUuid,
            workUuid,
            personUuid,
            cancellationToken
        );
        return await work.TryPublishAsync(
            workUuid,
            async (c, t, ct) =>
            {
                var stage = await applications.RequireIdentityStageAsync(
                    c,
                    t,
                    applicationAttemptUuid,
                    workUuid,
                    personUuid,
                    ct
                );
                await PeopleResolutionPolicyService.RequireActiveAsync(c, t, stage.Policy, ct);
                var publication = stage.Identity!;
                var creation = stage
                    .Decisions.Where(decision =>
                        decision.Action == PeopleResolutionAction.CreateIdentity
                    )
                    .Select(decision => decision.Creation)
                    .Distinct()
                    .SingleOrDefault(plan => plan?.PersonUuid == personUuid);
                if (
                    creation == null
                    || creation.Kind is not ("person" or "group")
                    || publication.People.Count != 1
                    || publication.People[0]
                        != new CanonicalPerson(personUuid, creation.DisplayName, creation.Kind)
                    || publication.Credits.Count != 0
                    || publication.ExternalIdentities.Count != 0
                    || publication.Aliases.Count == 0
                    || publication.Aliases.Any(alias =>
                        alias.PersonUuid != personUuid || alias.Basis != "validated_match"
                    )
                )
                {
                    throw new ArgumentException(
                        "Identity publication requires its evaluated creation plan and evidenced aliases."
                    );
                }
                await ApplyPeoplePublication(c, t, workUuid, publication, ct);
                await applications.WriteStagePublishedAsync(
                    c,
                    t,
                    applicationAttemptUuid,
                    workUuid,
                    null,
                    ct
                );
            },
            cancellationToken,
            candidateWriteScopes: IdentityCandidateWriteScopes
        );
    }

    /// <summary>
    /// Publishes managed contributor support from the application's authenticated credit
    /// stage. Safe additions do not authorize retirement of an incomplete role or file scope.
    /// Actual accepted, pending or preserved outcomes are saved in the same transaction.
    /// </summary>
    public async Task<NormalizationPublicationResult> PublishResolutionCreditsAsync(
        Guid workUuid,
        Guid applicationAttemptUuid,
        CancellationToken cancellationToken = default
    )
    {
        var applications = new PeopleAcceptanceApplicationStore(db);
        await RequireResolutionStageRequest(
            applications,
            applicationAttemptUuid,
            workUuid,
            null,
            cancellationToken
        );
        return await work.TryPublishAsync(
            workUuid,
            async (c, t, ct) =>
            {
                var stage = await applications.RequireCreditStageAsync(
                    c,
                    t,
                    applicationAttemptUuid,
                    workUuid,
                    ct
                );
                await PeopleResolutionPolicyService.RequireActiveAsync(c, t, stage.Policy, ct);
                var reconciliation = stage.Reconciliation!;
                if (
                    reconciliation
                        .Contributors.SelectMany(value => value.Credits)
                        .Any(credit =>
                            credit.Basis != "validated_match" || credit.State != "accepted"
                        )
                )
                {
                    throw new ArgumentException(
                        "Resolution credits require evaluated automatic authority."
                    );
                }
                var outcomes = await ApplyCreditReconciliation(
                    c,
                    t,
                    workUuid,
                    reconciliation,
                    true,
                    ct,
                    actorContextStage: stage,
                    applicationAttemptUuid: applicationAttemptUuid
                );
                await applications.WriteStagePublishedAsync(
                    c,
                    t,
                    applicationAttemptUuid,
                    workUuid,
                    outcomes,
                    ct
                );
            },
            cancellationToken,
            candidateWriteScopes: IdentityCandidateWriteScopes
        );
    }

    // Work terminal replay does not invoke its callback. Bind the public request first so an
    // unrelated completed Work UUID cannot be presented as this application's publication.
    private static async Task RequireResolutionStageRequest(
        PeopleAcceptanceApplicationStore applications,
        Guid applicationAttemptUuid,
        Guid workUuid,
        Guid? personUuid,
        CancellationToken ct
    )
    {
        var application =
            await applications.GetApplicationAsync(applicationAttemptUuid, ct)
            ?? throw new ArgumentException("Unknown people acceptance application.");
        var stage = application.Stages.SingleOrDefault(value => value.WorkUuid == workUuid);
        if (
            stage == null
            || stage.Ticket == null
            || stage.PersonUuid != personUuid
            || (personUuid.HasValue ? stage.Identity == null : stage.Reconciliation == null)
        )
        {
            throw new NormalizationConflictException(
                "Publication does not match the application's admitted stage."
            );
        }
    }
}
