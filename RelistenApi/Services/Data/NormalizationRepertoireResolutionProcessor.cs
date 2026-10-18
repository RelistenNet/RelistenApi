using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace Relisten.Data;

/// <summary>
/// Resumes durable repertoire phases. A source registration owns refresh enrollment; an artist
/// owns accepted music. The temporary artist contribution is released before source acknowledgment.
/// </summary>
public sealed partial class NormalizationRepertoireResolutionProcessor(
    DbService db,
    NormalizationRepertoireSourceService sources,
    NormalizationCandidateService candidates,
    RepertoireResolutionPolicyService policies,
    NormalizationRepertoireResolutionStore store,
    NormalizationWorkService work,
    NormalizedMusicService music
)
{
    private readonly RepertoireResolutionPolicyEvaluator evaluator = new();

    public const string Version =
        "repertoire-resolution-v1/"
        + NormalizationRecordingProcessor.Version
        + "/"
        + NormalizationSetlistProcessor.Version
        + "/"
        + NormalizationCandidateService.FactsVersion;

    public async Task<RepertoireResolutionAttempt> ResolveAsync(
        Guid attemptUuid,
        RepertoireResolutionRequest request,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        if (attemptUuid == Guid.Empty || request.ExpectedArtistUuid == Guid.Empty)
        {
            throw new ArgumentException("Nonempty attempt and expected-artist UUIDs are required.");
        }
        var attempt = await store.GetProcessingAsync(attemptUuid, ct);
        if (attempt != null)
        {
            if (!Same(attempt.Request, request))
            {
                throw new NormalizationConflictException(
                    "The repertoire attempt belongs to another source request."
                );
            }
            return await AdvanceAsync(attempt, ct);
        }

        // Authenticate immutable coordinates before creating a registration. Failure here cannot
        // manufacture a source subscription or an empty retained input.
        var source = await sources.ReadAsync(request, ct);
        attempt = await store.GetOrCreateAsync(
            attemptUuid,
            request,
            Version,
            source.Lineage,
            null,
            ct
        );
        return await PrepareAsync(attempt, source, ct);
    }

    public Task<RepertoireResolutionAttempt?> GetAsync(
        Guid attemptUuid,
        CancellationToken ct = default
    ) => store.GetAsync(attemptUuid, ct);

    public Task<RepertoireResolutionRegistration?> GetRegistrationAsync(
        Guid registrationUuid,
        CancellationToken ct = default
    ) => store.GetRegistrationAsync(registrationUuid, ct);

    public Task<RepertoireResolutionRecoveryPage> GetRecoveryAttemptsAsync(
        int limit = 100,
        Guid? afterUuid = null,
        CancellationToken ct = default
    ) => store.GetRecoveryAttemptsAsync(limit, afterUuid, ct);

    public Task<RepertoireResolutionDecisionPage> GetDecisionsAsync(
        Guid attemptUuid,
        int limit = 100,
        Guid? afterClaimUuid = null,
        CancellationToken ct = default
    ) => store.GetDecisionsAsync(attemptUuid, limit, afterClaimUuid, ct);

    public Task<RepertoireResolutionRegistrationPage> GetPendingRegistrationsAsync(
        int limit = 100,
        Guid? afterRegistrationUuid = null,
        CancellationToken ct = default
    ) => store.GetPendingRegistrationsAsync(limit, afterRegistrationUuid, ct);

    public async Task<RepertoireResolutionAttempt> RetryAsync(
        Guid newAttemptUuid,
        Guid registrationUuid,
        CancellationToken ct = default
    )
    {
        var registration =
            await store.GetRegistrationAsync(registrationUuid, ct)
            ?? throw new ArgumentException("Unknown repertoire registration.");
        var existing = await store.GetProcessingAsync(newAttemptUuid, ct);
        if (existing != null)
        {
            if (
                existing.Registration.RegistrationUuid != registrationUuid
                || existing.PreviousAttemptUuid == null
            )
            {
                throw new NormalizationConflictException(
                    "The retry UUID belongs to another registration or original request."
                );
            }
            return await AdvanceAsync(existing, ct);
        }
        var previous =
            await store.GetLatestAttemptAsync(registrationUuid, ct)
            ?? throw new NormalizationConflictException(
                "The registration's prior attempt is unavailable."
            );
        if (newAttemptUuid == previous.AttemptUuid || newAttemptUuid == Guid.Empty)
        {
            throw new ArgumentException("A repertoire retry requires a new attempt UUID.");
        }
        var request = await sources.RefreshRequestAsync(registration, previous.Request, ct);
        var source = await sources.ReadAsync(request, ct);
        if (source.Lineage != registration.Lineage)
        {
            throw new NormalizationConflictException(
                "A retry cannot change its authenticated source lineage."
            );
        }
        var attempt = await store.GetOrCreateAsync(
            newAttemptUuid,
            request,
            Version,
            source.Lineage,
            previous.AttemptUuid,
            ct
        );
        return await PrepareAsync(attempt, source, ct);
    }

    private async Task<RepertoireResolutionAttempt> PrepareAsync(
        RepertoireResolutionAttempt attempt,
        RepertoireResolutionSourceSnapshot source,
        CancellationToken ct
    )
    {
        var policy = await policies.ReadStateAsync(ct);
        return await store.SavePreparationAsync(attempt, new(source, policy), ct);
    }

    private async Task<RepertoireResolutionAttempt> AdvanceAsync(
        RepertoireResolutionAttempt attempt,
        CancellationToken ct
    )
    {
        if (attempt.Result != null)
        {
            return attempt with { Result = attempt.Result with { Replayed = true } };
        }
        try
        {
            if (attempt.ProcessingVersion != Version)
            {
                return await AdvanceIncompatibleAttemptAsync(attempt, ct);
            }
            if (attempt.Preparation == null)
            {
                return await PrepareAsync(
                    attempt,
                    await sources.ReadAsync(attempt.Request, ct),
                    ct
                );
            }
            var pending = FindPendingStage(attempt);
            if (pending != null)
            {
                return await AdvanceStageAsync(attempt, pending, ct);
            }
            var lastWrite = FindWriteNeedingRetirement(attempt);
            if (lastWrite != null)
            {
                return await PrepareRetirementAsync(attempt, lastWrite, ct);
            }
            if (attempt.Stages.Any(stage => stage.FailureReason != null))
            {
                return await store.CompleteAsync(
                    attempt,
                    RepertoireResolutionDisposition.Stale,
                    "stage_observation_changed_after_cleanup",
                    ct
                );
            }
            var pages = attempt
                .Stages.Where(stage => stage.Kind == RepertoireResolutionStageKind.DecisionPage)
                .ToArray();
            var completePages = Math.Max(
                1,
                (attempt.Preparation.Source.Claims.Count + attempt.Preparation.PageSize - 1)
                    / attempt.Preparation.PageSize
            );
            var unwritten = FindUnwrittenPage(attempt, pages);
            if (unwritten != null)
            {
                return await SaveAcceptedStageAsync(attempt, unwritten, ct);
            }
            if (pages.Length < completePages)
            {
                return await PreparePageAsync(attempt, pages.Length, ct);
            }
            if (
                !attempt.Stages.Any(stage =>
                    stage.Kind == RepertoireResolutionStageKind.AcknowledgeSource
                )
            )
            {
                return await PrepareAcknowledgmentAsync(attempt, ct);
            }
            return await store.CompleteAsync(
                attempt,
                RepertoireResolutionDisposition.Completed,
                "source_decisions_acknowledged",
                ct
            );
        }
        catch (NormalizationConflictException)
        {
            return await RecordConflictAsync(attempt, ct);
        }
    }

    private async Task<RepertoireResolutionAttempt> AdvanceIncompatibleAttemptAsync(
        RepertoireResolutionAttempt attempt,
        CancellationToken ct
    )
    {
        var pending = FindPendingStage(attempt);
        if (pending is { Kind: RepertoireResolutionStageKind.RetireArtistContribution })
        {
            return await AdvanceStageAsync(attempt, pending, ct);
        }
        if (pending is { Kind: RepertoireResolutionStageKind.AcceptedWrite, Ticket: not null })
        {
            return await store.FailStageAsync(
                attempt,
                pending.StageUuid,
                "processing_version_changed",
                ct
            );
        }
        var write = FindWriteNeedingRetirement(attempt);
        if (write != null)
        {
            // Version incompatibility revokes interpretation authority, not the obligation
            // to release an already installed contribution using its durable ticket.
            return await PrepareRetirementAsync(attempt, write, ct);
        }
        return await store.CompleteAsync(
            attempt,
            RepertoireResolutionDisposition.Stale,
            "processing_version_changed",
            ct
        );
    }

    private static RepertoireResolutionStage? FindPendingStage(
        RepertoireResolutionAttempt attempt
    ) =>
        attempt.Stages.FirstOrDefault(stage =>
            stage.Kind != RepertoireResolutionStageKind.DecisionPage
            && stage.Receipt == null
            && !stage.Superseded
            && stage.FailureReason == null
        );

    private static RepertoireResolutionStage? FindWriteNeedingRetirement(
        RepertoireResolutionAttempt attempt
    )
    {
        var write = attempt.Stages.LastOrDefault(stage =>
            stage.Kind == RepertoireResolutionStageKind.AcceptedWrite
            && (
                stage.Ticket != null
                || stage.Receipt?.Status == NormalizationPublicationStatus.Published
            )
        );
        return
            write != null
            && !attempt.Stages.Any(stage =>
                stage.Kind == RepertoireResolutionStageKind.RetireArtistContribution
                && stage.PageIndex == write.PageIndex
                && (
                    stage.Receipt?.Status == NormalizationPublicationStatus.Published
                    || stage.Superseded
                )
            )
            ? write
            : null;
    }

    private static RepertoireResolutionStage? FindUnwrittenPage(
        RepertoireResolutionAttempt attempt,
        IReadOnlyList<RepertoireResolutionStage> pages
    ) =>
        pages.LastOrDefault(page =>
            !page.ContextOmitted
            && HasChanges(BuildPublication(page.Context, page.Decisions))
            && !attempt.Stages.Any(stage =>
                stage.Kind == RepertoireResolutionStageKind.AcceptedWrite
                && stage.PageIndex == page.PageIndex
            )
        );

    private async Task<RepertoireResolutionAttempt> RecordConflictAsync(
        RepertoireResolutionAttempt attempt,
        CancellationToken ct
    )
    {
        var current = await store.GetProcessingAsync(attempt.AttemptUuid, ct) ?? attempt;
        if (current.CheckpointRevision != attempt.CheckpointRevision)
        {
            // A conflict against an older image cannot terminalize another caller's progress.
            return current;
        }
        var admittedArtist = attempt.Stages.LastOrDefault(stage =>
            stage.Receipt == null
            && !stage.Superseded
            && stage.FailureReason == null
            && (
                stage.Kind == RepertoireResolutionStageKind.RetireArtistContribution
                || stage.Kind == RepertoireResolutionStageKind.AcceptedWrite && stage.Ticket != null
            )
        );
        return admittedArtist != null
            ? await store.FailStageAsync(
                attempt,
                admittedArtist.StageUuid,
                "original_observation_changed",
                ct
            )
            : await store.CompleteAsync(
                attempt,
                RepertoireResolutionDisposition.Stale,
                "original_observation_changed",
                ct
            );
    }

    private async Task<RepertoireResolutionAttempt> PreparePageAsync(
        RepertoireResolutionAttempt attempt,
        int pageIndex,
        CancellationToken ct
    )
    {
        var preparation = attempt.Preparation!;
        var context = await candidates.ReadRepertoireResolutionContextAsync(
            attempt.Registration.RegistrationUuid,
            preparation.Source,
            pageIndex,
            preparation.PageSize,
            preparation.Policy,
            ct
        );
        if (!context.FreshnessComplete)
        {
            throw new NormalizationConflictException(
                "The source context lacks required registered owner or catalog clocks."
            );
        }
        var decisions = context
            .Claims.Select(claim => evaluator.Decide(claim, preparation.Policy.Active))
            .ToArray();
        var page = new RepertoireResolutionStage(
            Guid.NewGuid(),
            Guid.NewGuid(),
            RepertoireResolutionStageKind.DecisionPage,
            pageIndex,
            ObservationOwner(attempt.Registration.RegistrationUuid),
            context,
            new(),
            decisions,
            context.ReadSet
        );
        return await store.SaveStageAsync(attempt, page, ct);
    }

    private async Task<RepertoireResolutionAttempt> SaveAcceptedStageAsync(
        RepertoireResolutionAttempt attempt,
        RepertoireResolutionStage page,
        CancellationToken ct
    )
    {
        var context = page.Context;
        if (context.ArtistUuid == null || context.ArtistOwner == null)
        {
            throw new NormalizationConflictException(
                "Accepted repertoire changes require a checked artist owner."
            );
        }
        var owner = new NormalizationSubjectKey(
            "songs",
            "artist_repertoire",
            context.ArtistUuid.Value
        );
        var read = RepertoireResolutionReadSets.Merge(
            context.ReadSet,
            PeerReadSet(context.ArtistOwner, ArtistContributionKey(context.ArtistUuid.Value))
        );
        var stage = new RepertoireResolutionStage(
            Guid.NewGuid(),
            Guid.NewGuid(),
            RepertoireResolutionStageKind.AcceptedWrite,
            page.PageIndex,
            owner,
            context,
            BuildPublication(context, page.Decisions),
            page.Decisions,
            read
        );
        return await store.SaveStageAsync(attempt, stage, ct);
    }

    private async Task<RepertoireResolutionAttempt> PrepareRetirementAsync(
        RepertoireResolutionAttempt attempt,
        RepertoireResolutionStage write,
        CancellationToken ct
    )
    {
        write =
            await store.GetStageAsync(attempt.AttemptUuid, write.StageUuid, ct)
            ?? throw new NormalizationConflictException(
                "The admitted artist stage is unavailable."
            );
        var preparation = attempt.Preparation!;
        var context = await candidates.ReadRepertoireResolutionRetirementContextAsync(
            attempt.Registration.RegistrationUuid,
            write.Owner.Uuid,
            preparation.Policy,
            ct
        );
        var ownerContext =
            context.ArtistOwner
            ?? throw new NormalizationConflictException(
                "The accepted artist owner disappeared before contribution retirement."
            );
        var key = ArtistContributionKey(context.ArtistUuid!.Value);
        if (!context.FreshnessComplete)
        {
            throw new NormalizationConflictException(
                "The retirement peer context lacks its original owner clocks."
            );
        }
        var read = PeerReadSet(ownerContext, key);
        var retirement = new RepertoireResolutionStage(
            Guid.NewGuid(),
            Guid.NewGuid(),
            RepertoireResolutionStageKind.RetireArtistContribution,
            write.PageIndex,
            write.Owner,
            context,
            new(),
            [],
            read
        );
        var saved = await store.SaveStageAsync(attempt, retirement, ct);
        var currentContribution = ownerContext.Membership.Contributions.SingleOrDefault(x =>
            x.Contributor == key
        );
        var expectedRevision = MaterializerRevisionAfterAdmissions(attempt, write);
        if (currentContribution == null || currentContribution.Revision != expectedRevision)
        {
            // A newer accepted stage replaced these transient keys. It owns their retirement;
            // clearing them here could remove another attempt's original observations.
            return await store.MarkStageSupersededAsync(saved, retirement.StageUuid, ct);
        }
        return saved;
    }

    private async Task<RepertoireResolutionAttempt> PrepareAcknowledgmentAsync(
        RepertoireResolutionAttempt attempt,
        CancellationToken ct
    )
    {
        var preparation = attempt.Preparation!;
        var decisions = attempt
            .Stages.Where(stage => stage.Kind == RepertoireResolutionStageKind.DecisionPage)
            .SelectMany(stage => stage.Decisions)
            .ToArray();
        var context = await candidates.ReadRepertoireResolutionCompletionContextAsync(
            attempt.Registration.RegistrationUuid,
            attempt.AttemptUuid,
            preparation.Source,
            preparation.Policy,
            decisions,
            ct
        );
        if (
            !context.FreshnessComplete
            || context.Issues.Any(issue =>
                issue.StartsWith("accepted_correspondence_", StringComparison.Ordinal)
            )
        )
        {
            throw new NormalizationConflictException(
                "The complete source acknowledgment lacks a current accepted context."
            );
        }
        var key = ObservationContributionKey(attempt.Registration.RegistrationUuid);
        var read = RepertoireResolutionReadSets.Merge(
            context.ReadSet,
            PeerReadSet(context.ObservationOwner, key)
        );
        var stage = new RepertoireResolutionStage(
            Guid.NewGuid(),
            Guid.NewGuid(),
            RepertoireResolutionStageKind.AcknowledgeSource,
            -1,
            ObservationOwner(attempt.Registration.RegistrationUuid),
            context,
            new(),
            [],
            read
        );
        return await store.SaveStageAsync(attempt, stage, ct);
    }

    private async Task<RepertoireResolutionAttempt> AdvanceStageAsync(
        RepertoireResolutionAttempt attempt,
        RepertoireResolutionStage stage,
        CancellationToken ct
    )
    {
        if (stage.Ticket == null)
        {
            var ticket = await AdmitStageAsync(attempt, stage, ct);
            try
            {
                NormalizationRepertoireResolutionStore.RequireTicket(
                    stage,
                    ticket,
                    attempt.ProcessingVersion
                );
                return await store.SaveTicketAsync(
                    attempt.AttemptUuid,
                    stage.StageUuid,
                    ticket,
                    ct
                );
            }
            catch (NormalizationConflictException)
                when (stage.Kind
                        is RepertoireResolutionStageKind.AcceptedWrite
                            or RepertoireResolutionStageKind.RetireArtistContribution
                )
            {
                // Work admission is its own committed transaction. Retain the actual installed
                // ticket even when a same-byte recapture changed its generation, so cleanup
                // can release this attempt's contribution without claiming accepted authority.
                return await store.FailAdmissionAsync(
                    attempt,
                    stage.StageUuid,
                    ticket,
                    "admission_read_set_changed",
                    ct
                );
            }
        }
        var receipt =
            stage.Kind == RepertoireResolutionStageKind.AcknowledgeSource
                ? await PublishAcknowledgmentAsync(attempt, stage, ct)
                : await music.PublishRepertoireResolutionAsync(
                    stage.WorkUuid,
                    attempt.AttemptUuid,
                    stage.StageUuid,
                    ct
                );
        if (receipt.Status == NormalizationPublicationStatus.Stale)
        {
            return
                stage.Kind == RepertoireResolutionStageKind.AcceptedWrite
                || stage.Kind == RepertoireResolutionStageKind.RetireArtistContribution
                ? await store.FailStageAsync(
                    attempt,
                    stage.StageUuid,
                    "publication_observation_changed",
                    ct
                )
                : await store.CompleteAsync(
                    attempt,
                    RepertoireResolutionDisposition.Stale,
                    "publication_observation_changed",
                    ct
                );
        }
        return (await store.GetProcessingAsync(attempt.AttemptUuid, ct))!;
    }

    internal static long MaterializerRevisionAfterAdmissions(
        RepertoireResolutionAttempt attempt,
        RepertoireResolutionStage fullWrite
    )
    {
        var key = ArtistContributionKey(fullWrite.Owner.Uuid);
        var expected =
            (
                fullWrite
                    .Context.ArtistOwner!.Membership.Contributions.SingleOrDefault(value =>
                        value.Contributor == key
                    )
                    ?.Revision
                ?? 0
            ) + 1;
        foreach (
            var cleanup in attempt.Stages.Where(value =>
                value.Kind == RepertoireResolutionStageKind.RetireArtistContribution
                && value.PageIndex == fullWrite.PageIndex
                && value.Ticket != null
                && value.FailureReason != null
            )
        )
        {
            var before =
                cleanup
                    .Context.ArtistOwner!.Membership.Contributions.SingleOrDefault(value =>
                        value.Contributor == key
                    )
                    ?.Revision
                ?? 0;
            if (before != expected)
            {
                throw new NormalizationConflictException(
                    "The failed cleanup no longer proves ownership of the materializer contribution."
                );
            }
            expected = before + 1;
        }
        return expected;
    }

    internal static NormalizationSubjectKey ObservationOwner(Guid registrationUuid) =>
        new("songs", "repertoire_observation", registrationUuid);

    internal static NormalizationResourceKey ArtistContributionKey(Guid artistUuid) =>
        new("relisten", "repertoire_materializer", artistUuid.ToString("D"));

    internal static NormalizationResourceKey ObservationContributionKey(Guid registrationUuid) =>
        new("relisten", "repertoire_resolution", registrationUuid.ToString("D"));

    private static bool Same<T>(T left, T right) =>
        JsonElement.DeepEquals(
            JsonSerializer.SerializeToElement(left),
            JsonSerializer.SerializeToElement(right)
        );

    private static bool HasChanges(MusicPublication value) =>
        value.RepertoireSongs.Count + value.ImportedSongs.Count + value.ExternalIdentities.Count
        > 0;
}
