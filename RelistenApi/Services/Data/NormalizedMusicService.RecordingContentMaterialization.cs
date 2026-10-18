using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizedMusicService
{
    /// <summary>
    /// Only immutable processor decisions supply a complete content image. Identifiers are
    /// authenticated before Work's replay shortcut, then source and authority are checked again
    /// inside the atomic accepted-row/correspondence/receipt publication transaction.
    /// </summary>
    public async Task<NormalizationPublicationResult> PublishRecordingContentMaterializationAsync(
        Guid workUuid,
        Guid attemptUuid,
        CancellationToken ct = default
    )
    {
        var store = new NormalizationRecordingContentMaterializationStore(db);
        var original =
            await store.GetProcessingAsync(attemptUuid, ct)
            ?? throw new ArgumentException("Unknown recording content materialization attempt.");
        RequireRecordingPublicationCoordinates(original, workUuid);
        return await work.TryPublishAsync(
            workUuid,
            async (c, t, token) =>
            {
                var attempt = await store.RequireForPublicationAsync(
                    c,
                    t,
                    attemptUuid,
                    workUuid,
                    token
                );
                RequireRecordingPublicationCoordinates(attempt, workUuid);
                var preparation = attempt.Preparation!;
                var context = preparation.Context!;
                await new NormalizationRepertoireResolutionStore(
                    db
                ).RequireCompletedConsumptionAsync(c, t, context.Consumption, token);
                await RepertoireConsumerPolicyService.RequireStateAsync(
                    c,
                    t,
                    context.Policy,
                    token
                );
                var current =
                    await NormalizationRecordingContentMaterializer.ReadContextInSnapshotAsync(
                        c,
                        t,
                        context.Consumption,
                        token
                    );
                // Owner membership and admission state changed when this exact ticket was installed.
                // Accepted rows, ledger heads and authority must still reproduce the saved decisions.
                var evaluated = NormalizationRecordingContentMaterializer.BuildPreparation(
                    current with
                    {
                        OwnerContext = context.OwnerContext,
                        ReadSet = context.ReadSet,
                        InputReferences = context.InputReferences,
                    },
                    preparation.Decisions
                );
                if (
                    !NormalizationRepertoireResolutionStore.Same(preparation.Image, evaluated.Image)
                    || !NormalizationRepertoireResolutionStore.Same(
                        preparation.Decisions,
                        evaluated.Decisions
                    )
                    || !NormalizationRepertoireResolutionStore.Same(
                        preparation.CorrespondenceContexts,
                        evaluated.CorrespondenceContexts
                    )
                )
                {
                    throw new NormalizationConflictException(
                        "The prepared complete file image or correspondence authority changed."
                    );
                }
                var publication = new MusicPublication
                {
                    TrackContents = preparation.Image!.Contents,
                };
                await ValidateOwnership(c, t, attempt.Ticket!.SubjectUuid, publication, token);
                await ValidateDependencies(c, t, attempt.Ticket, publication, token);
                await WriteAutomaticRecordingAuthorizationsAsync(c, t, attempt, token);
                if (publication.TrackContents.Count > 0)
                {
                    await Apply(c, t, publication, workUuid, false, token);
                }
                await store.WriteCorrespondenceAsync(c, t, attempt, token);
                await ConsumeRecordingWithdrawalsAsync(c, t, attempt, token);
                await store.WritePublishedAsync(
                    c,
                    t,
                    attemptUuid,
                    new(workUuid, NormalizationPublicationStatus.Published, false),
                    token
                );
            },
            ct
        );
    }

    internal static string RecordingContentClaimHash(TrackContentChange change) =>
        ClaimHash(change);

    private static void RequireRecordingPublicationCoordinates(
        RecordingContentMaterializationAttempt attempt,
        Guid workUuid
    )
    {
        if (
            workUuid == Guid.Empty
            || attempt.AttemptUuid != workUuid
            || attempt.Ticket?.WorkUuid != workUuid
            || attempt.AdmissionFailureReason != null
            || attempt.Preparation
                is not { ContextOmitted: false, Context: { } context, Image: { } image }
            || image.SourceUuid != attempt.Registration.SourceUuid
            || context.SourceUuid != image.SourceUuid
            || context.Consumption.Recording?.SourceUuid != context.SourceUuid
            || context.Consumption.Attempt.AttemptUuid != attempt.SelectedRepertoireAttemptUuid
        )
        {
            throw new NormalizationConflictException(
                "Recording publication requires this exact admitted complete processor attempt."
            );
        }
        NormalizationRecordingContentMaterializationStore.RequireTicket(attempt, attempt.Ticket);
    }

    private static async Task WriteAutomaticRecordingAuthorizationsAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        RecordingContentMaterializationAttempt attempt,
        CancellationToken ct
    )
    {
        foreach (
            var decision in attempt.Preparation!.Decisions.Where(decision =>
                decision.Evaluation?.Disposition == MusicUsageDecisionDisposition.Authorized
            )
        )
        {
            if (
                decision.ContentUuid is not { } target
                || decision.ExistingAuthorizationUuid != null
            )
            {
                throw new NormalizationConflictException(
                    "Evaluated authority requires its exact target and exclusive processor provenance."
                );
            }
            var authorization = new RecordingContentAuthorizationRecord(
                decision.DecisionUuid,
                attempt.Registration.SourceUuid,
                decision.Source,
                [new(target, decision.ExpectedRevision)],
                SetlistAuthorizationAction.Authorize,
                decision.Withdrawal,
                decision.Evidence,
                ProcessorDecisionUuid: decision.DecisionUuid
            );
            await WriteRecordingAuthorizationAsync(
                c,
                t,
                authorization,
                null,
                [attempt.Preparation.Context!.Consumption.Recording!.Ticket],
                ct
            );
        }
    }

    private static async Task ConsumeRecordingWithdrawalsAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        RecordingContentMaterializationAttempt attempt,
        CancellationToken ct
    )
    {
        foreach (var withdrawal in attempt.Preparation!.Image!.Withdrawals)
        {
            await c.ExecuteAsync(
                new CommandDefinition(
                    """
                    INSERT INTO normalization.recording_content_removal_authorizations_used(revision_uuid,authorization_uuid,target_uuid)
                    SELECT revision_uuid,@AuthorizationUuid,uuid FROM normalization.track_contents WHERE uuid=@ContentUuid
                    """,
                    withdrawal,
                    t,
                    cancellationToken: ct
                )
            );
        }
    }
}
