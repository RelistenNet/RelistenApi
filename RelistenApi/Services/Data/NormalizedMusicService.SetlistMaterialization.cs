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
    /// <summary>Only saved consumer identifiers cross this boundary; caller-supplied images grant no authority.</summary>
    public async Task<NormalizationPublicationResult> PublishSetlistMaterializationAsync(
        Guid workUuid,
        Guid attemptUuid,
        CancellationToken ct = default
    )
    {
        var materializations = new NormalizationSetlistMaterializationStore(db);
        var original =
            await materializations.GetProcessingAsync(attemptUuid, ct)
            ?? throw new ArgumentException("Unknown setlist materialization attempt.");
        RequireSetlistMaterialization(original, workUuid);
        return await work.TryPublishAsync(
            workUuid,
            async (c, t, token) =>
            {
                var attempt = await materializations.RequireForPublicationAsync(
                    c,
                    t,
                    attemptUuid,
                    workUuid,
                    token
                );
                var saved = RequireSetlistMaterialization(attempt, workUuid);
                RequireSetlistMaterializationDecisions(attempt);
                var context = saved.Context!;
                await new NormalizationRepertoireResolutionStore(
                    db
                ).RequireCompletedConsumptionAsync(c, t, context.Consumption, token);
                await RepertoireConsumerPolicyService.RequireStateAsync(
                    c,
                    t,
                    context.Policy,
                    token
                );
                await NormalizationSetlistMaterializer.RequireAcceptedContextAsync(
                    c,
                    t,
                    context,
                    attempt.Registration,
                    token
                );
                var proposal = NormalizationSetlistMaterializer.Decide(
                    context with
                    {
                        ReviewedClaimExclusions = [],
                    },
                    saved.Decisions
                );
                var exclusions =
                    await NormalizationSetlistMaterializer.ReadReviewedClaimExclusionsAsync(
                        c,
                        t,
                        proposal,
                        token
                    );
                if (
                    !NormalizationSetlistMaterializer.Same(
                        exclusions,
                        context.ReviewedClaimExclusions
                    )
                )
                {
                    throw new NormalizationConflictException(
                        "The reviewed exclusions for the complete setlist proposal changed."
                    );
                }
                if (saved.Image is { } image)
                {
                    if (context.Owner.Kind != "setlist")
                    {
                        throw new NormalizationConflictException(
                            "An unbound observer may only acknowledge its source account."
                        );
                    }
                    var publication = StructurePublication(image);
                    var authority = new SetlistMaterializationPublicationAuthority(attempt);
                    await AppendSetlistMaterializationAuthoritiesAsync(c, t, attempt, token);
                    await ValidateOwnership(c, t, attempt.Ticket!.SubjectUuid, publication, token);
                    await ValidateDependencies(c, t, attempt.Ticket, publication, token, authority);
                    await Apply(c, t, publication, workUuid, false, token, authority);
                }
                // Empty observations and semantic no-ops still publish a real causal watermark.
                // Their historical accepted image is deliberately left untouched.
                await materializations.WritePublishedAsync(
                    c,
                    t,
                    attemptUuid,
                    new(workUuid, NormalizationPublicationStatus.Published, false),
                    token,
                    saved.RequiresObservationRetirement
                        ? null
                        : NormalizationSetlistMaterializer.CompletedResult(attempt)
                );
            },
            ct,
            candidateWriteScopes: []
        );
    }

    internal static string[] SetlistMaterializationClaimHashes(SetlistStructureChange image) =>
        Changes(StructurePublication(image))
            .Select(ClaimHash)
            .Distinct()
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

    private static SetlistMaterializationPreparation RequireSetlistMaterialization(
        SetlistMaterializationAttempt attempt,
        Guid workUuid
    )
    {
        var saved = NormalizationSetlistMaterializer.RequirePreparation(attempt);
        var context = saved.Context!;
        var expectedOwner =
            context.Consumption.Source.ArtistUuid == null
                ? NormalizationSetlistMaterializer.ObservationOwner(
                    attempt.Registration.ProcessingRegistrationUuid
                )
                : new NormalizationSubjectKey("songs", "setlist", context.EditionUuid);
        if (
            attempt.AttemptUuid != workUuid
            || attempt.Ticket?.WorkUuid != workUuid
            || attempt.AdmissionFailureReason != null
            || context.Owner != expectedOwner
            || context.Consumption.Setlist?.RegistrationUuid
                != attempt.Registration.ProcessingRegistrationUuid
            || attempt.SelectedRepertoireAttemptUuid != context.Consumption.Attempt.AttemptUuid
            || attempt.Ticket.PolicyVersion != attempt.ProcessingVersion
            || saved.RequiresObservationRetirement != (saved.ContributionsToRetire.Count > 0)
            || !NormalizationSetlistMaterializer.Same(
                saved.ContributionsToRetire,
                context.ContributionsToRetire
            )
            || !NormalizationSetlistMaterializer.AdmissionMatches(
                attempt.Ticket,
                context.ReadSet,
                context.OwnerContext
            )
        )
        {
            throw new NormalizationConflictException(
                "Publication requires this setlist consumer's exact admitted source and owner."
            );
        }
        return saved;
    }

    internal static void RequireSetlistMaterializationDecisions(
        SetlistMaterializationAttempt attempt
    )
    {
        var saved = NormalizationSetlistMaterializer.RequirePreparation(attempt);
        if (attempt.ProcessingVersion != NormalizationSetlistMaterializer.Version)
        {
            throw new NormalizationConflictException(
                "Fresh acceptance requires the supported saved setlist consumer version."
            );
        }
        var expected = NormalizationSetlistMaterializer.Decide(saved.Context!, saved.Decisions);
        if (!NormalizationSetlistMaterializer.Same(saved, expected))
        {
            throw new NormalizationConflictException(
                "The setlist image and decisions do not follow the complete authenticated source context."
            );
        }
    }

    private static async Task AppendSetlistMaterializationAuthoritiesAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SetlistMaterializationAttempt attempt,
        CancellationToken ct
    )
    {
        var saved = NormalizationSetlistMaterializer.RequirePreparation(attempt);
        var sourceTickets = saved.Context!.Structure is { } previous
            ? (await ReadStructureProposals(c, t, previous.Value, ct))
                .Values.Select(value => value.Ticket)
                .Append(saved.Context.Consumption.Setlist!.Ticket)
                .DistinctBy(value => value.WorkUuid)
                .OrderBy(value => value.WorkUuid)
                .ToArray()
            : new[] { saved.Context.Consumption.Setlist!.Ticket };
        foreach (
            var decision in saved.Decisions.Where(value =>
                value.Evaluation != null
                && value.Disposition
                    is MusicMaterializationDecisionDisposition.Accepted
                        or MusicMaterializationDecisionDisposition.Withdrawn
            )
        )
        {
            var authorization = NormalizationSetlistMaterializer.PurposeUuid(
                decision.DecisionUuid,
                "authorization"
            );
            var (key, kind, payload) = ProcessorSetlistAuthorization(decision, authorization);
            var head = await ReadSetlistAuthorizationHead(
                c,
                t,
                saved.Context!.EditionUuid,
                key,
                ct
            );
            if (head != null)
            {
                throw new NormalizationConflictException(
                    "Evaluated authority cannot replace a later approval, block or revocation."
                );
            }
            await c.ExecuteAsync(
                new CommandDefinition(
                    """
                    INSERT INTO normalization.setlist_authorization_events(uuid,edition_uuid,processor_decision_uuid,authorization_key,kind,action,
                        expected_structure_revision,expected_edition_revision,payload,source_tickets)
                    VALUES(@authorization,@edition,@decisionUuid,@key,@kind,'authorize',@structureRevision,@editionRevision,@payload::jsonb,@tickets::jsonb);
                    INSERT INTO normalization.setlist_authorization_heads(edition_uuid,authorization_key,authorization_uuid)
                    VALUES(@edition,@key,@authorization);
                    """,
                    new
                    {
                        authorization,
                        edition = saved.Context.EditionUuid,
                        decisionUuid = decision.DecisionUuid,
                        key,
                        kind,
                        structureRevision = saved.Context.Structure?.Revision ?? 0,
                        editionRevision = saved.Context.Edition?.Revision ?? 0,
                        payload,
                        tickets = JsonSerializer.Serialize(sourceTickets),
                    },
                    t,
                    cancellationToken: ct
                )
            );
        }
    }

    private static (string Key, string Kind, string Payload) ProcessorSetlistAuthorization(
        SetlistMaterializationDecision decision,
        Guid authorization
    )
    {
        if (decision is SetlistClaimMaterializationDecision claim)
        {
            var approval = new SetlistCorrespondenceApproval(
                authorization,
                claim.Source,
                claim.Claim,
                SetlistAuthorizationAction.Authorize,
                claim.Targets,
                null,
                claim.Evidence
            );
            return (
                ClaimKey(claim.Source, claim.Claim),
                "correspondence",
                JsonSerializer.Serialize(approval)
            );
        }
        if (decision is SetlistRemovalMaterializationDecision removal)
        {
            var approval = new SetlistRemovalApproval(
                authorization,
                removal.RemovalKind,
                removal.Target,
                SetlistAuthorizationAction.Authorize,
                removal.Sources,
                null,
                removal.Evidence
            );
            return (
                RemovalKey(removal.RemovalKind, removal.Target.Uuid),
                "removal",
                JsonSerializer.Serialize(approval)
            );
        }
        throw new NormalizationConflictException(
            "Only explicit saved correspondence or withdrawal decisions can grant evaluated authority."
        );
    }

    /// <summary>
    /// A historical processor authorization remains a receipt for an actual accepted write.
    /// Replaying it never reactivates policy: latest heads still control blocks and revocations.
    /// </summary>
    private static async Task RequireProcessorSetlistAuthorizationAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SetlistAuthorizationRow authorization,
        CancellationToken ct,
        SetlistMaterializationPublicationAuthority? authenticatedSetlistAttempt = null
    )
    {
        if (authorization.OperationUuid.HasValue == authorization.ProcessorDecisionUuid.HasValue)
        {
            throw new NormalizationConflictException(
                "Setlist authority requires exactly one reviewed operation or processor decision."
            );
        }
        if (authorization.ProcessorDecisionUuid is not { } decisionUuid)
        {
            return;
        }
        if (
            authenticatedSetlistAttempt?.VerifiedAuthorizations.Contains(
                authorization.AuthorizationUuid
            ) == true
        )
        {
            return;
        }
        var alreadyAuthenticated = false;
        SetlistMaterializationPreparation preparation;
        SetlistMaterializationDecision decision;
        if (
            authenticatedSetlistAttempt?.CurrentDecisions.TryGetValue(
                decisionUuid,
                out var currentDecision
            ) == true
        )
        {
            // The dedicated writer already authenticated this entire saved attempt in this
            // transaction. Its authority rows must be consumable before its receipt commits.
            preparation = authenticatedSetlistAttempt.Attempt!.Preparation!;
            decision = currentDecision;
            alreadyAuthenticated = true;
        }
        else if (
            authenticatedSetlistAttempt?.HistoricalDecisions.TryGetValue(
                decisionUuid,
                out var retained
            ) == true
        )
        {
            preparation = retained.Preparation;
            decision = retained.Decision;
            alreadyAuthenticated = true;
        }
        else
        {
            var row = await c.QuerySingleOrDefaultAsync<ProcessorAuthorizationProof>(
                new CommandDefinition(
                    """
                    SELECT decision.attempt_uuid AS "AttemptUuid",decision.decision::text AS "Decision",COALESCE(octet_length(attempt.preparation::text),0)::bigint AS "PreparationBytes",
                        (attempt.receipt IS NOT NULL AND run.state='published' AND run.ticket=attempt.ticket
                            AND attempt.admission_failure_reason IS NULL AND attempt.receipt->>'WorkUuid'=run.uuid::text
                            AND (attempt.receipt->>'Status')::integer=0) AS "Published"
                    FROM normalization.setlist_materialization_decisions decision
                    JOIN normalization.setlist_materialization_attempts attempt ON attempt.attempt_uuid=decision.attempt_uuid
                    JOIN normalization.work_runs run ON run.uuid=attempt.attempt_uuid
                    WHERE decision.uuid=@decisionUuid
                    """,
                    new { decisionUuid },
                    t,
                    cancellationToken: ct
                )
            );
            if (row?.Published != true || row.PreparationBytes == 0)
            {
                throw new NormalizationConflictException(
                    "Processor authority requires its actual immutable accepted publication receipt."
                );
            }
            var proofBytes =
                (authenticatedSetlistAttempt?.RetainedProofBytes ?? 0) + row.PreparationBytes;
            if (proofBytes > 128 * 1024 * 1024)
            {
                throw new MusicMaterializationCapacityException(
                    "The complete historical setlist authority exceeds 128 MiB."
                );
            }
            var json = await c.QuerySingleAsync<string>(
                new CommandDefinition(
                    "SELECT preparation::text FROM normalization.setlist_materialization_attempts WHERE attempt_uuid=@uuid",
                    new { uuid = row.AttemptUuid },
                    t,
                    cancellationToken: ct
                )
            );
            preparation = JsonSerializer.Deserialize<SetlistMaterializationPreparation>(
                json,
                NormalizationSetlistMaterializationStore.StoredJsonOptions
            )!;
            if (authenticatedSetlistAttempt != null)
            {
                authenticatedSetlistAttempt.RetainedProofBytes = proofBytes;
            }
            decision = JsonSerializer.Deserialize<SetlistMaterializationDecision>(
                row.Decision,
                NormalizationSetlistMaterializationStore.StoredJsonOptions
            )!;
        }
        if (
            preparation.ContextOmitted
            || preparation.Context == null
            || preparation.Image == null
            || decision.Evaluation?.Disposition != MusicUsageDecisionDisposition.Authorized
            || decision.DecisionUuid != decisionUuid
            || !preparation.Decisions.Any(value =>
                NormalizationSetlistMaterializer.Same(value, decision)
            )
            || authorization.EditionUuid != preparation.Context.EditionUuid
            || authorization.AuthorizationUuid
                != NormalizationSetlistMaterializer.PurposeUuid(decisionUuid, "authorization")
        )
        {
            throw new NormalizationConflictException(
                "The processor authority does not name an accepted evaluated decision."
            );
        }
        var expected = alreadyAuthenticated
            ? preparation
            : NormalizationSetlistMaterializer.Decide(preparation.Context, preparation.Decisions);
        var (key, kind, payload) = ProcessorSetlistAuthorization(
            decision,
            authorization.AuthorizationUuid
        );
        if (
            !NormalizationSetlistMaterializer.Same(preparation, expected)
            || key != authorization.AuthorizationKey
            || kind != authorization.Kind
            || authorization.Action != "authorize"
            || !JsonElement.DeepEquals(
                JsonSerializer.Deserialize<JsonElement>(payload),
                JsonSerializer.Deserialize<JsonElement>(authorization.Payload)
            )
        )
        {
            throw new NormalizationConflictException(
                "The persisted processor authority differs from its re-evaluated original proof."
            );
        }
        if (!alreadyAuthenticated)
        {
            await RequireSetlistProcessorSourceTicketsAsync(
                c,
                t,
                preparation,
                authorization.SourceTickets,
                ct
            );
        }
        if (authenticatedSetlistAttempt != null)
        {
            authenticatedSetlistAttempt.VerifiedAuthorizations.Add(authorization.AuthorizationUuid);
            if (!alreadyAuthenticated)
            {
                foreach (var item in preparation.Decisions)
                {
                    authenticatedSetlistAttempt.HistoricalDecisions[item.DecisionUuid] = new(
                        preparation,
                        item
                    );
                }
            }
        }
    }

    private static async Task RequireSetlistProcessorSourceTicketsAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SetlistMaterializationPreparation preparation,
        string sourceTickets,
        CancellationToken ct
    )
    {
        var expected = (preparation.Context!.Structure?.Value.Sources ?? [])
            .Select(value => value.ProposalAttemptUuid)
            .Append(preparation.Context.Consumption.Setlist!.AttemptUuid)
            .ToHashSet();
        var tickets = JsonSerializer.Deserialize<NormalizationWorkTicket[]>(sourceTickets)!;
        if (
            tickets.Length != expected.Count
            || !expected.SetEquals(tickets.Select(value => value.WorkUuid))
        )
        {
            throw new NormalizationConflictException(
                "Processor authority requires every original before and after source ticket."
            );
        }
        var valid = await c.ExecuteScalarAsync<int>(
            new CommandDefinition(
                """
                SELECT count(*)::integer FROM jsonb_array_elements(@sourceTickets::jsonb) ticket
                JOIN normalization.setlist_processing_attempts source ON source.attempt_uuid=(ticket->>'WorkUuid')::uuid
                    AND source.disposition='published' AND source.batch->'Ticket'=ticket
                JOIN normalization.work_runs run ON run.uuid=source.attempt_uuid AND run.state='published' AND run.ticket=ticket
                """,
                new { sourceTickets },
                t,
                cancellationToken: ct
            )
        );
        if (valid != tickets.Length)
        {
            throw new NormalizationConflictException(
                "Processor authority names an unavailable or unauthenticated original source publication."
            );
        }
    }

    private sealed class SetlistMaterializationPublicationAuthority(
        SetlistMaterializationAttempt? attempt
    )
    {
        internal SetlistMaterializationAttempt? Attempt { get; } = attempt;
        internal Dictionary<Guid, SetlistStructureSet> AcceptedSets { get; } =
            attempt?.Preparation?.Context?.Sets.ToDictionary(
                value => value.Value.Uuid,
                value => value.Value
            ) ?? [];
        internal Dictionary<Guid, SetlistMaterializationDecision> CurrentDecisions { get; } =
            attempt?.Preparation?.Decisions.ToDictionary(value => value.DecisionUuid) ?? [];
        internal Dictionary<Guid, AuthenticatedSetlistDecision> HistoricalDecisions { get; } = [];
        internal HashSet<Guid> VerifiedAuthorizations { get; } = [];
        internal long RetainedProofBytes { get; set; }
    }

    private sealed record AuthenticatedSetlistDecision(
        SetlistMaterializationPreparation Preparation,
        SetlistMaterializationDecision Decision
    );

    private sealed record ProcessorAuthorizationProof(
        Guid AttemptUuid,
        string Decision,
        long PreparationBytes,
        bool Published
    );
}
