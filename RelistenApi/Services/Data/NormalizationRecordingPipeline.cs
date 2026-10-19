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
/// Delivers one committed recording handoff through the existing extraction, people,
/// source-alias and venue owners. Work enrollment precedes handoff acknowledgement,
/// so interruption after extraction cannot lose the unfinished downstream work.
/// This service supplies no facts, winners, policies or authority to domain writers.
/// </summary>
public sealed partial class NormalizationRecordingPipeline(
    NormalizationRecordingLookupStore lookups,
    NormalizationRecordingLookupDispatcher dispatcher,
    NormalizationRecordingProposalService extractions,
    NormalizationPeopleResolutionProcessor peopleResolver,
    NormalizationPeopleResolutionApplier peopleApplier,
    SourceActorResolutionService aliasResolver,
    SourceActorAliasApplicationService aliasApplier,
    NormalizationVenueResolutionProcessor venueResolver,
    NormalizationEvidenceService evidence,
    NormalizationProviderDecoder decoder,
    NormalizationWorkService work,
    PeopleResolutionPolicyService peoplePolicies,
    VenueResolutionPolicyService venuePolicies
)
{
    public const string Version =
        "recording-normalization-pipeline-v1/"
        + NormalizationRecordingProcessor.Version
        + "/"
        + NormalizationPeopleResolutionProcessor.Version
        + "/"
        + NormalizationPeopleResolutionApplier.Version
        + "/"
        + SourceActorResolutionService.Version
        + "/"
        + SourceActorAliasApplicationService.Version
        + "/"
        + NormalizationVenueResolutionProcessor.Version;
    public const string OwnerDomain = "extractions";
    public const string OwnerKind = "recording_pipeline";

    /// <summary>Identifies unfinished delivery independently of the legacy source UUID.</summary>
    public static NormalizationSubjectKey Owner(Guid handoffUuid) =>
        new(OwnerDomain, OwnerKind, handoffUuid);

    /// <summary>
    /// Reuse the request after cancellation or a transient exception. Domain checkpoints
    /// retain every committed stage. A returned policy/review/unresolved outcome is parked,
    /// not a request for immediate retry; changed facts need a new attempt UUID. Changed
    /// provider evidence must first produce a new immutable lookup handoff.
    /// </summary>
    public async Task<NormalizationRecordingPipelineResult> ProcessAsync(
        NormalizationRecordingPipelineRequest request,
        CancellationToken ct = default
    )
    {
        if (request.AttemptUuid == Guid.Empty || request.HandoffUuid == Guid.Empty)
        {
            throw new ArgumentException("A pipeline attempt and committed handoff are required.");
        }

        var handoff =
            await lookups.GetHandoffAsync(request.HandoffUuid, ct)
            ?? throw new ArgumentException("Unknown recording lookup handoff.");
        var lookup = (await lookups.GetAttemptAsync(handoff.LookupAttemptUuid, ct))!;
        var binding = lookup.Preparation!.Binding!;
        var peoplePolicy = await peoplePolicies.ReadStateAsync(ct);
        var venuePolicy = await venuePolicies.ReadStateAsync(ct);
        var ticket = await work.BeginWorkAsync(
            request.AttemptUuid,
            Owner(request.HandoffUuid),
            Version,
            binding.Inputs.Select(input => input.Resource).Distinct().ToArray(),
            [peoplePolicy.OwnerObservation.SubjectUuid, venuePolicy.OwnerObservation.SubjectUuid],
            // The handoff fixes these resources and owner identities. Keep the admission
            // request identical on replay rather than substituting a later owner CAS.
            null,
            ct
        );

        var delivery = await dispatcher.DispatchAsync(request.HandoffUuid, ct);
        var extraction = delivery.ProcessingResult;
        if (extraction.Disposition != NormalizationRecordingProcessingDisposition.Published)
        {
            return Result(
                extraction.Disposition == NormalizationRecordingProcessingDisposition.Stale
                    ? NormalizationRecordingPipelineDisposition.Stale
                    : NormalizationRecordingPipelineDisposition.Unresolved,
                extraction.Reason
            );
        }

        // Enrollment captures current heads for recovery, not permission to replace the
        // handoff's original evidence. An old handoff cannot acknowledge a newer head.
        if (
            binding.Inputs.Any(input =>
                !ticket.Inputs.Any(value => value.RevisionUuid == input.InputRevisionUuid)
            )
        )
        {
            return Result(NormalizationRecordingPipelineDisposition.Stale, "handoff_input_changed");
        }

        var batch = (await extractions.GetAttemptBatchAsync(extraction.AttemptUuid, ct))!;
        var (peopleResolution, people) = await ResolvePeopleAsync(request, batch, ct);
        var (aliasResolution, aliases) = await ResolveAliasesAsync(request, batch, ct);
        var venues = await ResolveVenuesAsync(request, batch, ct);
        var disposition = CompletionDisposition(
            peopleResolution,
            people,
            aliasResolution,
            aliases,
            venues,
            peoplePolicy.Active != null,
            venuePolicy.Active != null
        );
        NormalizationPublicationResult? completion = null;
        if (disposition == NormalizationRecordingPipelineDisposition.Completed)
        {
            completion = await work.TryPublishAsync(
                ticket.WorkUuid,
                (_, _, _) => Task.CompletedTask,
                ct
            );
            if (completion.Status == NormalizationPublicationStatus.Stale)
            {
                disposition = NormalizationRecordingPipelineDisposition.Stale;
            }
        }

        return new(
            request,
            extraction.SourceUuid,
            disposition,
            disposition switch
            {
                NormalizationRecordingPipelineDisposition.Completed => "completed",
                NormalizationRecordingPipelineDisposition.AwaitingPolicy =>
                    "acceptance_policy_inactive",
                NormalizationRecordingPipelineDisposition.Review => "review_required",
                NormalizationRecordingPipelineDisposition.Stale => "stage_read_set_changed",
                _ => "stage_unresolved",
            },
            extraction,
            peopleResolution,
            people,
            aliasResolution,
            aliases,
            venues,
            completion
        );

        NormalizationRecordingPipelineResult Result(
            NormalizationRecordingPipelineDisposition state,
            string reason
        ) =>
            new(
                request,
                extraction.SourceUuid,
                state,
                reason,
                extraction,
                null,
                null,
                null,
                null,
                []
            );
    }

    private async Task<(
        NormalizationPeopleResolutionResult Resolution,
        PeopleAcceptanceResult? Application
    )> ResolvePeopleAsync(
        NormalizationRecordingPipelineRequest request,
        NormalizationRecordingProposalBatch batch,
        CancellationToken ct
    )
    {
        var resolution = await peopleResolver.ResolveRecordingAsync(
            Child(request, "people_resolution"),
            batch.AttemptUuid,
            ct
        );
        var application =
            resolution.Result!.Disposition == NormalizationPeopleResolutionDisposition.Published
                ? (
                    await peopleApplier.ApplyRecordingAsync(
                        Child(request, "people_application"),
                        resolution.AttemptUuid,
                        ct
                    )
                ).Result
                : null;
        return (resolution.Result, application);
    }

    private async Task<(
        SourceActorResolutionResult Resolution,
        SourceActorAliasApplicationResult? Application
    )> ResolveAliasesAsync(
        NormalizationRecordingPipelineRequest request,
        NormalizationRecordingProposalBatch batch,
        CancellationToken ct
    )
    {
        var resolution = await aliasResolver.ResolveAsync(
            Child(request, "alias_resolution"),
            batch.AttemptUuid,
            ct
        );
        var application =
            resolution.Result!.Disposition == SourceActorResolutionDisposition.Published
                ? (
                    await aliasApplier.ApplyAsync(
                        Child(request, "alias_application"),
                        resolution.AttemptUuid,
                        ct
                    )
                ).Result
                : null;
        return (resolution.Result, application);
    }

    private static NormalizationRecordingPipelineDisposition CompletionDisposition(
        NormalizationPeopleResolutionResult peopleResolution,
        PeopleAcceptanceResult? people,
        SourceActorResolutionResult aliasResolution,
        SourceActorAliasApplicationResult? aliases,
        IReadOnlyList<VenueResolutionResult> venues,
        bool peoplePolicyActive,
        bool venuePolicyActive
    )
    {
        if (
            peopleResolution.Disposition == NormalizationPeopleResolutionDisposition.Stale
            || people?.Disposition == PeopleAcceptanceDisposition.Stale
            || aliasResolution.Disposition == SourceActorResolutionDisposition.Stale
            || aliases?.Disposition == SourceActorAliasApplicationDisposition.Stale
            || venues.Any(value => value.Disposition == VenueResolutionDisposition.Stale)
        )
        {
            return NormalizationRecordingPipelineDisposition.Stale;
        }
        if (!peoplePolicyActive || (venues.Count != 0 && !venuePolicyActive))
        {
            return NormalizationRecordingPipelineDisposition.AwaitingPolicy;
        }
        if (
            people?.Disposition == PeopleAcceptanceDisposition.Review
            || aliases?.Disposition
                is SourceActorAliasApplicationDisposition.Review
                    or SourceActorAliasApplicationDisposition.Partial
            || venues.Any(value => value.Disposition == VenueResolutionDisposition.Review)
        )
        {
            return NormalizationRecordingPipelineDisposition.Review;
        }
        return
            people?.Disposition == PeopleAcceptanceDisposition.Applied
            && aliases?.Disposition == SourceActorAliasApplicationDisposition.Complete
            && venues.All(value => value.Disposition == VenueResolutionDisposition.Applied)
            ? NormalizationRecordingPipelineDisposition.Completed
            : NormalizationRecordingPipelineDisposition.Unresolved;
    }

    private static Guid Child(NormalizationRecordingPipelineRequest request, string phase) =>
        new(
            SHA256
                .HashData(
                    Encoding.UTF8.GetBytes(
                        Version
                            + "\n"
                            + request.AttemptUuid.ToString("D")
                            + "\n"
                            + request.HandoffUuid.ToString("D")
                            + "\n"
                            + phase
                    )
                )
                .AsSpan(0, 16)
        );
}
