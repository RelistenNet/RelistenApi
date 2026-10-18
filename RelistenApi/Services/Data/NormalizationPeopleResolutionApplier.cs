using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace Relisten.Data;

/// <summary>
/// Applies independently evaluated people decisions through the existing identity and credit
/// owners. Every publication has its own immutable preparation; retries resume its Work receipt.
/// </summary>
public sealed partial class NormalizationPeopleResolutionApplier(
    DbService db,
    NormalizationPeopleResolutionStore resolutions,
    NormalizationRecordingProposalService extractions,
    NormalizationCandidateService candidates,
    NormalizationCatalogBindingService bindings,
    NormalizedPeopleService people,
    NormalizationPeopleResolutionDecisionService baseline,
    PeopleResolutionPolicyEvaluator evaluator,
    PeopleResolutionPolicyService policies,
    PeopleAcceptanceApplicationStore store,
    NormalizationWorkService work
)
{
    public const string Version =
        "people-acceptance-v1/"
        + PeopleResolutionPolicyEvaluator.ScorerVersion
        + "/"
        + NormalizationCandidateService.FactsVersion
        + "/"
        + NormalizedPeopleService.CreditContextVersion
        + "/"
        + NormalizationRecordingExtractor.Version
        + "/retirement-coverage-v1/"
        + RecordingCreditDecodeIssues.Version
        + "/negative-decision-enrollment-v1";
    private readonly NormalizationEvidenceService evidence = new(db);
    private readonly NormalizationProviderDecoder decoder = new();

    /// <summary>
    /// Applies the exact published proposal. Callers cannot submit facts, winners or authority.
    /// Reuse the application UUID after interruption; use a new UUID for a changed proposal.
    /// </summary>
    public async Task<PeopleAcceptanceApplication> ApplyRecordingAsync(
        Guid applicationAttemptUuid,
        Guid resolutionAttemptUuid,
        CancellationToken ct = default
    )
    {
        var application = await store.GetApplicationAsync(applicationAttemptUuid, ct);
        if (application != null && application.ResolutionAttemptUuid != resolutionAttemptUuid)
        {
            throw new NormalizationConflictException(
                "Application UUID already belongs to another proposal."
            );
        }
        if (application?.Result != null)
        {
            return Replay(application);
        }
        application ??= await store.GetOrCreateAsync(
            applicationAttemptUuid,
            resolutionAttemptUuid,
            Version,
            ct
        );
        while (application.Result == null)
        {
            ct.ThrowIfCancellationRequested();
            if (application.Version != Version)
            {
                application = await store.CompleteAsync(
                    application,
                    PeopleAcceptanceDisposition.Unresolved,
                    "processing_version_changed",
                    ct
                );
                continue;
            }
            try
            {
                application =
                    application.Plan == null
                        ? await PrepareInitialPlanAsync(application, ct)
                        : await AdvanceApplicationAsync(application, ct);
            }
            catch (NormalizationConflictException)
            {
                application = await store.CompleteAsync(
                    application,
                    PeopleAcceptanceDisposition.Stale,
                    "acceptance_read_set_changed",
                    ct
                );
            }
        }
        return application;
    }

    public Task<PeopleAcceptanceApplication?> GetApplicationAsync(
        Guid applicationAttemptUuid,
        CancellationToken ct = default
    ) => store.GetApplicationAsync(applicationAttemptUuid, ct);

    private async Task<PeopleAcceptanceApplication> PrepareInitialPlanAsync(
        PeopleAcceptanceApplication application,
        CancellationToken ct
    )
    {
        var proposal = (
            await resolutions.GetAttemptBatchAsync(application.ResolutionAttemptUuid, ct)
        )!;
        var published = await resolutions.GetAttemptAsync(application.ResolutionAttemptUuid, ct);
        if (
            published?.Result?.Disposition != NormalizationPeopleResolutionDisposition.Published
            || proposal.ProcessingVersion != NormalizationPeopleResolutionProcessor.Version
        )
        {
            return await store.CompleteAsync(
                application,
                PeopleAcceptanceDisposition.Unresolved,
                "unsupported_resolution_version",
                ct
            );
        }
        var extraction = await extractions.GetAttemptBatchAsync(proposal.ExtractionAttemptUuid, ct);
        if (extraction?.ProcessingVersion != NormalizationRecordingProcessor.Version)
        {
            return await store.CompleteAsync(
                application,
                PeopleAcceptanceDisposition.Unresolved,
                "unsupported_extraction_version",
                ct
            );
        }
        // A negative outcome must consume the same original evidence as an accepted write.
        // Enrolling newer provider clocks would acknowledge work that was never evaluated.
        await RevalidateOriginalInputsAsync(proposal, ct);
        var authenticated = await ReadDerivedFactsAsync(proposal.Preparation, ct);
        if (!PeopleAcceptanceApplicationStore.Same(authenticated, proposal.Preparation))
        {
            throw new NormalizationConflictException(
                "Proposal facts do not match their authenticated original snapshots."
            );
        }
        var policyState = await policies.ReadStateAsync(ct);
        var active = policyState.Active;
        var negativeReason =
            active == null ? "acceptance_policy_inactive"
            : !PeopleResolutionPolicyEvaluator.Supports(active.Policy.Artifact, "retained_credit")
                ? "unsupported_policy_applicability"
            : null;
        if (negativeReason != null)
        {
            return await store.SavePlanAsync(
                application.AttemptUuid,
                new(proposal, active, [], policyState, negativeReason),
                ct
            );
        }
        var decisions = Evaluate(application.AttemptUuid, authenticated, active!);
        return await store.SavePlanAsync(
            application.AttemptUuid,
            new(proposal, active, decisions, policyState),
            ct
        );
    }

    private async Task<PeopleAcceptanceApplication> AdvanceApplicationAsync(
        PeopleAcceptanceApplication application,
        CancellationToken ct
    )
    {
        var pending = application.Stages.FirstOrDefault(stage => stage.Receipt == null);
        if (pending != null)
        {
            return await PublishStageAsync(application, pending, ct);
        }
        var creditStage = application.Stages.SingleOrDefault(stage => !stage.PersonUuid.HasValue);
        if (creditStage != null)
        {
            if (creditStage.DecisionOnly)
            {
                return await store.CompleteAsync(
                    application,
                    creditStage.Decisions.Any(value =>
                        value.Action == PeopleResolutionAction.Review
                    )
                        ? PeopleAcceptanceDisposition.Review
                        : PeopleAcceptanceDisposition.Unresolved,
                    application.Plan!.NegativeReason ?? "no_accepted_credit_actions",
                    ct
                );
            }
            var outcomes = creditStage.Receipt!.Credits;
            var disposition = PeopleAcceptanceDisposition.Applied;
            if (
                outcomes.Any(outcome => outcome.State != "accepted")
                || creditStage.Decisions.Any(decision =>
                    decision.Action == PeopleResolutionAction.Review
                )
            )
            {
                disposition = PeopleAcceptanceDisposition.Review;
            }
            else if (
                creditStage.Decisions.Any(decision =>
                    decision.Action == PeopleResolutionAction.Unresolved
                )
                || creditStage.Facts.Inputs.Any(input =>
                    input.UnresolvedCredits.Count != 0
                    || input.DecodeIssues.Any(RecordingCreditDecodeIssues.AffectsCredits)
                )
                || (creditStage.ScopeCoverage ?? []).Any(scope => !scope.Complete)
            )
            {
                disposition = PeopleAcceptanceDisposition.Unresolved;
            }

            return await store.CompleteAsync(
                application,
                disposition,
                "recording_credit_application",
                ct
            );
        }
        if (application.Plan!.NegativeReason != null)
        {
            var acknowledgment = await PrepareDecisionStageAsync(application, ct);
            return acknowledgment == null
                ? await store.CompleteAsync(
                    application,
                    PeopleAcceptanceDisposition.Unresolved,
                    "unsupported_recording_owner_inputs",
                    ct
                )
                : await store.SaveStageAsync(application.AttemptUuid, acknowledgment, ct);
        }
        var facts =
            application.Stages.Count == 0
                ? application.Plan!.Proposal.Preparation
                : await ReadDerivedFactsAsync(application.Plan!.Proposal.Preparation, ct);
        await RevalidateOriginalInputsAsync(application.Plan!.Proposal, ct);
        var decisions =
            application.Stages.Count == 0
                ? application.Plan!.Decisions
                : Evaluate(
                    application.AttemptUuid,
                    facts,
                    application.Plan!.Policy!,
                    application.Stages,
                    application.Plan.Decisions
                );
        var creation = decisions.FirstOrDefault(decision =>
            decision.Action == PeopleResolutionAction.CreateIdentity
            && !application.Stages.Any(stage => stage.PersonUuid == decision.Creation!.PersonUuid)
        );
        if (creation != null)
        {
            var stage = await PrepareIdentityStageAsync(
                application,
                facts,
                decisions,
                creation,
                ct
            );
            return stage == null
                ? await store.CompleteAsync(
                    application,
                    PeopleAcceptanceDisposition.Unresolved,
                    "unsupported_identity_owner_inputs",
                    ct
                )
                : await store.SaveStageAsync(application.AttemptUuid, stage, ct);
        }
        var credit = await PrepareCreditStageAsync(application, facts, decisions, ct);
        if (credit == null)
        {
            return await store.CompleteAsync(
                application,
                PeopleAcceptanceDisposition.Unresolved,
                "unsupported_recording_owner_inputs",
                ct
            );
        }
        return await store.SaveStageAsync(application.AttemptUuid, credit, ct);
    }

    private async Task<PeopleAcceptanceApplication> PublishStageAsync(
        PeopleAcceptanceApplication application,
        PeopleAcceptanceStage stage,
        CancellationToken ct
    )
    {
        if (stage.Ticket == null)
        {
            var owner = new NormalizationSubjectKey(
                "people",
                stage.PersonUuid.HasValue ? "person" : "recording",
                stage.PersonUuid ?? application.SourceUuid
            );
            var ticket = await work.BeginContributedBatchAsync(
                stage.WorkUuid,
                owner,
                application.Version,
                stage.Contributions,
                stage.ExpectedConfigurationGeneration,
                ct,
                stage.ExpectedContributions
            );
            application = await store.AttachTicketAsync(
                application.AttemptUuid,
                stage.WorkUuid,
                ticket,
                ct
            );
            if (application.Result != null)
            {
                return application;
            }
            stage = application.Stages.Single(value => value.WorkUuid == stage.WorkUuid);
        }
        var publication =
            stage.DecisionOnly
                ? await people.PublishResolutionDecisionAsync(
                    stage.WorkUuid,
                    application.AttemptUuid,
                    ct
                )
            : stage.PersonUuid.HasValue
                ? await people.PublishResolutionIdentityAsync(
                    stage.WorkUuid,
                    application.AttemptUuid,
                    stage.PersonUuid.Value,
                    ct
                )
            : await people.PublishResolutionCreditsAsync(
                stage.WorkUuid,
                application.AttemptUuid,
                ct
            );
        if (publication.Status == NormalizationPublicationStatus.Stale)
        {
            return await store.CompleteAsync(
                application,
                PeopleAcceptanceDisposition.Stale,
                "publication_changed",
                ct
            );
        }
        var published = (await store.GetApplicationAsync(application.AttemptUuid, ct))!;
        if (published.Stages.Single(value => value.WorkUuid == stage.WorkUuid).Receipt == null)
        {
            throw new NormalizationConflictException(
                "Published Work did not produce its accepted stage receipt."
            );
        }
        return published;
    }

    private IReadOnlyList<PeopleAcceptanceClaimDecision> Evaluate(
        Guid applicationUuid,
        NormalizationPeopleResolutionPreparation facts,
        ActivePeopleAcceptancePolicy policy,
        IReadOnlyList<PeopleAcceptanceStage>? stages = null,
        IReadOnlyList<PeopleAcceptanceClaimDecision>? initialDecisions = null
    )
    {
        var cardinality = facts.Inputs.Sum(input =>
            input.Claims.Count + input.UnresolvedCredits.Count
        );
        var creditCoverageComplete = !facts.Inputs.Any(input =>
            input.DecodeIssues.Any(RecordingCreditDecodeIssues.AffectsCredits)
        );
        return facts
            .Inputs.SelectMany(input => input.Claims)
            .Select(claim =>
            {
                var originalCreation = initialDecisions
                    ?.Single(decision => decision.ProposalUuid == claim.Proposal.ProposalUuid)
                    .Creation;
                var personUuid =
                    originalCreation?.PersonUuid
                    ?? StableUuid(applicationUuid, claim.Proposal.ProposalUuid, "person");
                var groupUuid =
                    originalCreation?.CoReferenceGroupUuid
                    ?? StableUuid(applicationUuid, claim.Proposal.ProposalUuid, "creation-group");
                var created =
                    stages?.Any(stage => stage.Receipt != null && stage.PersonUuid == personUuid)
                    == true;
                var decision = evaluator.Evaluate(
                    claim,
                    policy.Policy,
                    cardinality,
                    "retained_credit",
                    personUuid,
                    groupUuid,
                    created ? personUuid : null
                );
                if (
                    !creditCoverageComplete
                    && decision.Action == PeopleResolutionAction.CreateIdentity
                )
                {
                    return decision with
                    {
                        Action = PeopleResolutionAction.Unresolved,
                        Reason = "incomplete_credit_decode",
                        PersonUuid = null,
                        Creation = null,
                    };
                }

                return decision;
            })
            .ToArray();
    }

    private static Guid StableUuid(Guid applicationUuid, Guid entityUuid, string purpose) =>
        new(
            SHA256.HashData(
                Encoding.UTF8.GetBytes(
                    $"people-acceptance-v1/{applicationUuid:D}/{entityUuid:D}/{purpose}"
                )
            )[..16]
        );

    private static PeopleAcceptanceApplication Replay(PeopleAcceptanceApplication application) =>
        application with
        {
            Result = application.Result! with { Replayed = true },
        };
}
