using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace Relisten.Data;

/// <summary>
/// Publishes authenticated role-free source aliases through one original person per stage.
/// Saved preparations survive cancellation. Reuse IDs to resume; changed original inputs or
/// terminal stale facts need a new application. No caller supplies accepted facts or winners.
/// </summary>
public sealed partial class SourceActorAliasApplicationService(
    DbService db,
    SourceActorResolutionStore resolutions,
    NormalizationCandidateService candidates,
    PeopleResolutionPolicyService policies,
    SourceActorResolutionDecisionService decisions,
    SourceActorAliasApplicationStore store,
    NormalizedPeopleService people,
    NormalizationWorkService work
)
{
    public const string Version =
        "source-actor-alias-publication-v1/"
        + SourceActorResolutionService.Version
        + "/complete-person-map-v1/component-review-v1";

    /// <summary>Reads retained application headers and outcomes without evaluating or publishing new facts.</summary>
    public Task<SourceActorAliasApplication?> GetApplicationAsync(
        Guid applicationAttemptUuid,
        CancellationToken ct = default
    ) => store.GetApplicationAsync(applicationAttemptUuid, ct);

    /// <summary>
    /// Applies one published resolution, retaining partial outcomes. Reuse the application UUID
    /// to resume saved stages or replay its terminal result; a different resolution conflicts.
    /// Callers supply IDs only. Changed evidence after terminal work requires a new application.
    /// </summary>
    public async Task<SourceActorAliasApplication> ApplyAsync(
        Guid applicationAttemptUuid,
        Guid resolutionAttemptUuid,
        CancellationToken ct = default
    )
    {
        var application = await store.GetApplicationAsync(applicationAttemptUuid, ct);
        if (application != null && application.ResolutionAttemptUuid != resolutionAttemptUuid)
        {
            throw new NormalizationConflictException(
                "Application already belongs to another original source proposal."
            );
        }
        if (application?.Result != null)
        {
            return application with { Result = application.Result with { Replayed = true } };
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
                return await store.CompleteAsync(
                    application,
                    SourceActorAliasApplicationDisposition.Unresolved,
                    "unsupported_source_application_version",
                    [],
                    ct
                );
            }
            var pending = application.Stages.SingleOrDefault(stage =>
                stage.Receipt == null && stage.TerminalReason == null
            );
            if (pending != null)
            {
                // Do not catch a callback fault as an outcome. The immutable stage must remain
                // retryable after its transaction rolls back, including cancellation at commit.
                var stage =
                    await store.GetStageAsync(pending.StageUuid, ct)
                    ?? throw new InvalidOperationException(
                        "Pending source stage proof is missing."
                    );
                application = await PublishStageAsync(application, stage, ct);
                continue;
            }
            try
            {
                var proposal = await RequireProposal(application, ct);
                if (application.Plan == null)
                {
                    var source = await InSnapshot(
                        (c, t) => ReadTrustedSourceAsync(c, t, proposal, ct),
                        ct
                    );
                    application = await store.SavePlanAsync(
                        application.AttemptUuid,
                        new(
                            Proposal(proposal),
                            source.Claims,
                            source.Current ? null : "source_proof_is_historical"
                        ),
                        ct
                    );
                    continue;
                }
                var prepared = await InSnapshot(
                    (c, t) => PrepareNextStage(c, t, application, proposal, ct),
                    ct
                );
                if (prepared.Stage != null)
                {
                    application = await store.SaveStageAsync(
                        application.AttemptUuid,
                        prepared.Stage,
                        ct
                    );
                    continue;
                }
                application = await store.CompleteAsync(
                    application,
                    prepared.Disposition,
                    prepared.Reason,
                    prepared.Readiness,
                    ct
                );
            }
            catch (NormalizationConflictException)
            {
                application = await store.CompleteAsync(
                    application,
                    SourceActorAliasApplicationDisposition.Stale,
                    "source_application_preparation_changed",
                    [],
                    ct
                );
            }
        }
        return application;
    }

    private async Task<SourceActorAliasApplication> PublishStageAsync(
        SourceActorAliasApplication application,
        SourceActorAliasPersonStage stage,
        CancellationToken ct
    )
    {
        if (stage.Ticket == null)
        {
            NormalizationWorkTicket ticket;
            try
            {
                ticket = await work.BeginContributedBatchAsync(
                    stage.WorkUuid,
                    new("people", "person", stage.OriginalPersonUuid),
                    application.Version,
                    stage.Contributions,
                    stage.ExpectedConfigurationGeneration,
                    ct,
                    stage.ExpectedContributions
                );
            }
            catch (NormalizationConflictException)
            {
                if (!await AdmissionFactsChangedAsync(stage, ct))
                {
                    throw;
                }
                application = await store.MarkAdmissionStaleAsync(
                    application.AttemptUuid,
                    stage.StageUuid,
                    "source_admission_original_facts_changed",
                    ct
                );
                var retained = application.Stages.Single(value =>
                    value.StageUuid == stage.StageUuid
                );
                if (retained.TerminalReason == null)
                {
                    // A concurrent admitted Work run owns recovery. The outer loop reloads
                    // its exact saved ticket instead of terminalizing that winning stage.
                    return application;
                }
                return await store.CompleteAsync(
                    application,
                    SourceActorAliasApplicationDisposition.Stale,
                    "source_admission_original_facts_changed",
                    [],
                    ct
                );
            }
            application = await store.AttachTicketAsync(
                application.AttemptUuid,
                stage.StageUuid,
                ticket,
                ct
            );
        }
        var result = await people.PublishSourceActorAliasStageAsync(
            application.AttemptUuid,
            stage.StageUuid,
            ct
        );
        if (result.Status == NormalizationPublicationStatus.Stale)
        {
            application = await store.MarkStaleAsync(
                application.AttemptUuid,
                stage.StageUuid,
                "source_publication_facts_changed",
                ct
            );
            return await store.CompleteAsync(
                application,
                SourceActorAliasApplicationDisposition.Stale,
                "source_publication_facts_changed",
                [],
                ct
            );
        }
        var published = (await store.GetApplicationAsync(application.AttemptUuid, ct))!;
        if (published.Stages.Single(value => value.StageUuid == stage.StageUuid).Receipt == null)
        {
            throw new InvalidOperationException(
                "Published source alias Work lacks its atomic domain receipt."
            );
        }
        return published;
    }

    private async Task<SourceActorResolutionBatch> RequireProposal(
        SourceActorAliasApplication application,
        CancellationToken ct
    )
    {
        var attempt = await resolutions.GetAttemptAsync(application.ResolutionAttemptUuid, ct);
        var proposal = await resolutions.GetAttemptBatchAsync(
            application.ResolutionAttemptUuid,
            ct
        );
        if (
            attempt?.Result?.Disposition != SourceActorResolutionDisposition.Published
            || proposal == null
            || proposal.SourceUuid != application.SourceUuid
            || proposal.ProcessingVersion != SourceActorResolutionService.Version
        )
        {
            throw new NormalizationConflictException(
                "Source application requires its exact supported published original proposal."
            );
        }
        return proposal;
    }

    private Task<T> InSnapshot<T>(
        Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> read,
        CancellationToken ct
    ) =>
        db.WithWriteConnection(async connection =>
        {
            var c = (NpgsqlConnection)connection;
            await using var t = await c.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
            await c.ExecuteAsync(
                new CommandDefinition(
                    "SET TRANSACTION READ ONLY",
                    transaction: t,
                    cancellationToken: ct
                )
            );
            var result = await read(c, t);
            await t.CommitAsync(ct);
            return result;
        });

    private static SourceActorAliasProposalReference Proposal(
        SourceActorResolutionBatch proposal
    ) => new(proposal.AttemptUuid, proposal.ExtractionAttemptUuid, proposal.ProcessingVersion);

    internal static Guid StableUuid(Guid application, string purpose) =>
        new(
            SHA256.HashData(Encoding.UTF8.GetBytes($"source-alias-v1/{application:D}/{purpose}"))[
                ..16
            ]
        );

    private sealed record NextStage(
        SourceActorAliasPersonStage? Stage,
        SourceActorAliasApplicationDisposition Disposition,
        string Reason,
        IReadOnlyList<SourceActorAliasReplacementReadiness> Readiness
    );
}
