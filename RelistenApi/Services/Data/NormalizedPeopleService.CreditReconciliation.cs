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

public sealed partial class NormalizedPeopleService
{
    /// <summary>
    /// Replaces explicitly complete contributor claims from a published extraction. Other
    /// contributors and editorial decisions remain in force. An append-published claim has
    /// independent support and cannot be withdrawn by this operation.
    /// </summary>
    public Task<NormalizationPublicationResult> ReconcileRecordingCreditsAsync(
        Guid workUuid,
        RecordingCreditReconciliation reconciliation,
        CancellationToken cancellationToken = default
    )
    {
        // The async publication consumes one immutable request, even when callers supplied
        // mutable lists. Validation and support retirement must examine the same claims.
        var frozen = JsonSerializer.Deserialize<RecordingCreditReconciliation>(
            JsonSerializer.Serialize(reconciliation)
        )!;
        return PublishReconciliationAsync(workUuid, frozen, cancellationToken);
    }

    private Task<NormalizationPublicationResult> PublishReconciliationAsync(
        Guid workUuid,
        RecordingCreditReconciliation reconciliation,
        CancellationToken cancellationToken
    ) =>
        work.TryPublishAsync(
            workUuid,
            async (c, t, ct) =>
            {
                await ApplyCreditReconciliation(c, t, workUuid, reconciliation, false, ct);
            },
            cancellationToken,
            candidateWriteScopes: IdentityCandidateWriteScopes
        );

    // Both accepted pipelines share evidence validation, support ownership and reviewed-field
    // guards. Only an authenticated resolution stage may add support in an incomplete scope.
    private static async Task<
        IReadOnlyList<PeopleAcceptanceCreditWriteOutcome>
    > ApplyCreditReconciliation(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid workUuid,
        RecordingCreditReconciliation reconciliation,
        bool allowPartialAdditions,
        CancellationToken ct,
        PeopleAcceptanceStage? actorContextStage = null,
        Guid? applicationAttemptUuid = null
    )
    {
        await c.ExecuteAsync(
            new CommandDefinition(
                "SELECT pg_advisory_xact_lock_shared(hashtextextended('normalization:people:identity',0)); SELECT pg_advisory_xact_lock(hashtextextended(@key,0))",
                new { key = $"normalization:credits:{reconciliation.SourceUuid}" },
                t,
                cancellationToken: ct
            )
        );
        var batch = await ReadReconciliationExtraction(c, t, workUuid, reconciliation, ct);
        var credits = reconciliation
            .Contributors.SelectMany(contributor => contributor.Credits)
            .ToArray();
        await ValidatePeoplePublicationOwner(c, t, workUuid, new([], [], [], credits), ct);
        var owner = await ReadWorkContext(c, t, workUuid, ct);
        if (owner.Kind != "recording" || owner.EntityUuid != reconciliation.SourceUuid)
        {
            throw new ArgumentException("Credit reconciliation requires its recording owner.");
        }
        ActorContextPublication? actorContext = null;
        if (actorContextStage != null)
        {
            if (!allowPartialAdditions || applicationAttemptUuid == null)
            {
                throw new ArgumentException(
                    "Actor context requires the guarded accepted application stage."
                );
            }
            actorContext = PrepareActorContextPublication(
                workUuid,
                applicationAttemptUuid.Value,
                batch,
                actorContextStage,
                owner.Ticket
            );
        }
        await ValidatePublicationEvidenceAsync(
            c,
            t,
            workUuid,
            "people",
            credits.SelectMany(credit => credit.Evidence).ToArray(),
            ct
        );
        var inputs = NormalizationRecordingReadSet.Inputs(batch.Preparation);
        var groups = new[]
        {
            new NormalizationRecordingInputExtraction(
                batch.Preparation.Binding.InputRevisionUuid,
                batch.Extraction
            ),
        }
            .Concat(batch.AdditionalExtractions ?? [])
            .ToDictionary(group => group.InputRevisionUuid);
        if (
            reconciliation
                .Contributors.Select(replacement => replacement.Contributor)
                .Distinct()
                .Count() != reconciliation.Contributors.Count
        )
        {
            throw new ArgumentException("A contributor can be replaced only once per publication.");
        }
        var outcomes = new List<PeopleAcceptanceCreditWriteOutcome>();
        foreach (var replacement in reconciliation.Contributors)
        {
            var input =
                inputs.SingleOrDefault(input => input.Contributor == replacement.Contributor)
                ?? throw new ArgumentException(
                    "Replacement contributor is absent from the published extraction."
                );
            ValidateCompleteCreditScopes(
                reconciliation.SourceUuid,
                replacement,
                input,
                groups[input.Binding.InputRevisionUuid].Extraction,
                allowPartialAdditions
            );
            outcomes.AddRange(
                await ReplaceContributorCreditSupport(
                    c,
                    t,
                    workUuid,
                    reconciliation.SourceUuid,
                    replacement,
                    ct,
                    requireAcceptedForRetirement: allowPartialAdditions,
                    actorContext: actorContext
                )
            );
        }
        await RetireUnsupportedActorContexts(c, t, reconciliation.SourceUuid, ct);
        await RetireUnsupportedCredits(c, t, workUuid, reconciliation.SourceUuid, ct);
        return outcomes;
    }

    private static async Task<NormalizationRecordingProposalBatch> ReadReconciliationExtraction(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid workUuid,
        RecordingCreditReconciliation request,
        CancellationToken ct
    )
    {
        var json =
            await c.QuerySingleOrDefaultAsync<string>(
                new CommandDefinition(
                    "SELECT batch::text FROM normalization.recording_processing_attempts WHERE attempt_uuid=@ExtractionAttemptUuid AND source_uuid=@SourceUuid AND disposition='published'",
                    request,
                    t,
                    cancellationToken: ct
                )
            )
            ?? throw new ArgumentException(
                "Credit replacement requires a successfully published extraction."
            );
        var batch = JsonSerializer.Deserialize<NormalizationRecordingProposalBatch>(json)!;
        if (batch.ProcessingVersion != NormalizationRecordingProcessor.Version)
        {
            // Earlier terminal batches remain readable, but do not establish complete
            // file-credit coverage. A fresh extraction is required before withdrawal.
            throw new ArgumentException(
                "Credit replacement requires the current complete recording extraction version."
            );
        }
        var ticket = (await ReadWorkContext(c, t, workUuid, ct)).Ticket;
        var original = batch.Ticket;
        var consumed = ticket.Dependencies.Any(dependency =>
            dependency.SubjectUuid == original.SubjectUuid
            && dependency.DesiredGeneration == original.DesiredGeneration
            && dependency.EditorialRevision == original.EditorialRevision
            && dependency.PublishedWorkUuid == batch.AttemptUuid
        );
        if (
            !consumed
            || !original.Inputs.All(ticket.Inputs.Contains)
            || !(original.CatalogInputs ?? []).All((ticket.CatalogInputs ?? []).Contains)
            || !(original.SourceIdentities ?? []).All((ticket.SourceIdentities ?? []).Contains)
            || !(original.SourceUuids ?? []).All((ticket.SourceUuids ?? []).Contains)
            || (
                original.MappingRegistry != null
                && ticket.MappingRegistry != original.MappingRegistry
            )
        )
        {
            throw new ArgumentException(
                "Credit replacement must consume the original extraction and its complete input read set."
            );
        }
        return batch;
    }

    private static void ValidateCompleteCreditScopes(
        Guid sourceUuid,
        RecordingCreditContributorReplacement replacement,
        NormalizationRecordingPreparedInput input,
        NormalizationRecordingExtraction extraction,
        bool allowPartialAdditions = false
    )
    {
        var scopes = replacement.CompleteScopes.ToHashSet();
        if (
            scopes.Count != replacement.CompleteScopes.Count
            || scopes.Any(scope => string.IsNullOrWhiteSpace(scope.Role))
        )
        {
            throw new ArgumentException("Complete credit scopes must be distinct and name a role.");
        }
        Guid? Scope(ProviderObservationOrigin? origin)
        {
            if (origin == null)
            {
                return null;
            }
            return input
                .Binding.Files.SingleOrDefault(file => file.Observation.Origin == origin)
                ?.Binding.Value?.TrackUuid;
        }
        bool Unbound(ProviderObservationOrigin? origin) => origin != null && Scope(origin) == null;
        bool UnresolvedBlocksScope(
            RetainedCreditUnresolved issue,
            RecordingCreditCompleteScope scope
        )
        {
            // Guarded applications carry current-field coverage separately. A typed unknown
            // transferrer does not invalidate complete taper coverage; prose has no such role fence.
            var role = RetainedCreditFieldRole(issue.Evidence.JsonPointer);
            if (allowPartialAdditions && role != null && role != scope.Role)
            {
                return false;
            }
            return issue.FileOrigin == null && scope.ScopeTrackUuid == null
                || Unbound(issue.FileOrigin) && scope.ScopeTrackUuid != null
                || issue.FileOrigin != null && scope.ScopeTrackUuid == Scope(issue.FileOrigin);
        }
        if (
            (!allowPartialAdditions || scopes.Count != 0)
            && (
                (
                    allowPartialAdditions
                        ? extraction.DecodeIssues.Any(
                            Relisten.Normalization.RecordingCreditDecodeIssues.AffectsCredits
                        )
                        : extraction.DecodeIssues.Count != 0
                )
                || extraction.Credits.Any(credit => Unbound(credit.FileOrigin))
                    && scopes.Any(scope => scope.ScopeTrackUuid != null)
                || extraction.UnresolvedCredits.Any(issue =>
                    scopes.Any(scope => UnresolvedBlocksScope(issue, scope))
                )
            )
        )
        {
            throw new ArgumentException(
                "Incomplete credit extraction cannot authorize contributor withdrawal."
            );
        }
        var proposals = extraction
            .Credits.Where(proposal => !Unbound(proposal.FileOrigin))
            .Select(proposal => new
            {
                Proposal = proposal,
                Scope = new RecordingCreditCompleteScope(
                    CreditRole(proposal.Mention.Role),
                    Scope(proposal.FileOrigin)
                ),
            })
            .ToArray();
        bool Matches(
            RecordingCredit credit,
            NormalizationRecordingCreditProposal proposal,
            RecordingCreditCompleteScope scope
        ) =>
            credit.SourceUuid == sourceUuid
            && credit.Role == scope.Role
            && credit.ScopeTrackUuid == scope.ScopeTrackUuid
            && (
                credit.OriginalText == proposal.Mention.Name
                || credit.OriginalText == proposal.Mention.Evidence.Quote
                    && proposals.Count(other =>
                        other.Scope == scope
                        && other.Proposal.Mention.Evidence == proposal.Mention.Evidence
                    ) == 1
            )
            && credit.Evidence.Contains(proposal.Mention.Evidence);
        var revisions = input
            .Binding.Inputs.Select(reference => reference.InputRevisionUuid)
            .ToHashSet();
        if (
            replacement.Credits.Any(credit =>
                credit.Basis == "reviewed"
                || credit.State is not ("accepted" or "pending")
                || !allowPartialAdditions
                    && !scopes.Contains(new(credit.Role, credit.ScopeTrackUuid))
                || credit.Evidence.Any(evidence =>
                    !revisions.Contains(evidence.InputRevisionUuid)
                    || !proposals.Any(proposal =>
                        Matches(credit, proposal.Proposal, proposal.Scope)
                        && proposal.Proposal.Mention.Evidence == evidence
                    )
                )
                || !proposals.Any(proposal => Matches(credit, proposal.Proposal, proposal.Scope))
            )
            || proposals.Any(proposal =>
                scopes.Contains(proposal.Scope)
                && !replacement.Credits.Any(credit =>
                    Matches(credit, proposal.Proposal, proposal.Scope)
                )
            )
            || proposals.Any(proposal =>
                replacement
                    .Credits.Where(credit =>
                        credit.State == "accepted"
                        && Matches(credit, proposal.Proposal, proposal.Scope)
                    )
                    .Select(credit => credit.PersonUuid)
                    .Distinct()
                    .Count() > 1
            )
            || scopes.Any(scope =>
                scope.ScopeTrackUuid.HasValue
                && !input.Binding.Files.Any(file =>
                    file.Binding.Value?.TrackUuid == scope.ScopeTrackUuid
                )
            )
        )
        {
            throw new ArgumentException(
                "Every complete scope must preserve all its extracted mentions and checked file associations."
            );
        }
    }

    // A typed provider field establishes which role its unresolved value can affect. An
    // arbitrary prose field does not. Array entries retain their parent field's role.
    internal static string? RetainedCreditFieldRole(string pointer)
    {
        var slash = pointer.LastIndexOf('/');
        var field = pointer[(slash + 1)..];
        if (int.TryParse(field, out _) && slash > 0)
        {
            pointer = pointer[..slash];
            field = pointer[(pointer.LastIndexOf('/') + 1)..];
        }
        return field switch
        {
            "taper" or "tapers" => "taper",
            "transferrer" or "transferrers" or "transferer" => "transferrer",
            _ => null,
        };
    }

    private static string CreditRole(RecordingCreditRole role) =>
        role switch
        {
            RecordingCreditRole.MatrixBuilder => "matrix_builder",
            RecordingCreditRole.MasteringEngineer => "mastering_engineer",
            RecordingCreditRole.RecordingEngineer => "recording_engineer",
            _ => role.ToString().ToLowerInvariant(),
        };

    private static async Task<
        IReadOnlyList<PeopleAcceptanceCreditWriteOutcome>
    > ReplaceContributorCreditSupport(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid workUuid,
        Guid sourceUuid,
        RecordingCreditContributorReplacement replacement,
        CancellationToken ct,
        bool requireAcceptedForRetirement = false,
        ActorContextPublication? actorContext = null
    )
    {
        var kept = new HashSet<Guid>();
        var outcomes = new List<PeopleAcceptanceCreditWriteOutcome>();
        foreach (var incoming in replacement.Credits)
        {
            var credit = await HoldChangedReviewedField(c, t, incoming, ct);
            var written = await WriteCredit(
                c,
                t,
                credit,
                workUuid,
                false,
                0,
                ct,
                preserveExistingClaims: true
            );
            if (written == null)
            {
                outcomes.Add(
                    new(incoming.Uuid, null, null, "preserved", "reviewed_claim_preserved")
                );
                continue;
            }
            var actual = await c.QuerySingleAsync<CreditRow>(
                new CommandDefinition(
                    $"SELECT {CreditColumns} FROM normalization.recording_credits WHERE uuid=@Uuid",
                    new { written.Uuid },
                    t,
                    cancellationToken: ct
                )
            );
            outcomes.Add(
                new(incoming.Uuid, actual.Uuid, actual.PersonUuid, actual.State, actual.ReasonCode)
            );
            if (written.Created)
            {
                await c.ExecuteAsync(
                    new CommandDefinition(
                        "INSERT INTO normalization.recording_credit_managed(credit_uuid,introduced_work_uuid) VALUES(@Uuid,@workUuid) ON CONFLICT DO NOTHING",
                        new { written.Uuid, workUuid },
                        t,
                        cancellationToken: ct
                    )
                );
            }
            foreach (var evidence in credit.Evidence.Distinct())
            {
                var support = await c.QuerySingleAsync<Guid>(
                    new CommandDefinition(
                        """
                        WITH added AS (
                            INSERT INTO normalization.recording_credit_contributor_support(credit_uuid,contributor_provider,contributor_type,contributor_key,input_revision_uuid,json_pointer,start_code_point,end_code_point,quote,introduced_work_uuid)
                            VALUES(@Uuid,@Provider,@ResourceType,@ProviderKey,@InputRevisionUuid,@JsonPointer,@StartCodePoint,@EndCodePoint,@Quote,@workUuid)
                            ON CONFLICT DO NOTHING RETURNING uuid
                        ) SELECT uuid FROM added UNION ALL
                        SELECT uuid FROM normalization.recording_credit_contributor_support
                        WHERE credit_uuid=@Uuid AND contributor_provider=@Provider AND contributor_type=@ResourceType AND contributor_key=@ProviderKey
                          AND input_revision_uuid=@InputRevisionUuid AND json_pointer=@JsonPointer
                          AND start_code_point IS NOT DISTINCT FROM @StartCodePoint::integer
                          AND end_code_point IS NOT DISTINCT FROM @EndCodePoint::integer AND quote IS NOT DISTINCT FROM @Quote::text
                          AND retired_work_uuid IS NULL
                        """,
                        new
                        {
                            written.Uuid,
                            replacement.Contributor.Provider,
                            replacement.Contributor.ResourceType,
                            replacement.Contributor.ProviderKey,
                            evidence.InputRevisionUuid,
                            evidence.JsonPointer,
                            evidence.StartCodePoint,
                            evidence.EndCodePoint,
                            evidence.Quote,
                            workUuid,
                        },
                        t,
                        cancellationToken: ct
                    )
                );
                kept.Add(support);
                await WriteAcceptedActorContexts(
                    c,
                    t,
                    workUuid,
                    actorContext,
                    replacement.Contributor,
                    incoming,
                    actual,
                    support,
                    evidence,
                    ct
                );
            }
        }
        // Only a complete scope can retire support, and unchanged exact citations retain
        // their original receipt. Failure anywhere rolls these transitions back atomically.
        foreach (var scope in replacement.CompleteScopes)
        {
            // A successful identity proposal can still encounter a protected editorial
            // claim in the domain writer. Such a scope did not complete successfully.
            if (
                requireAcceptedForRetirement
                && replacement
                    .Credits.Where(credit =>
                        credit.Role == scope.Role && credit.ScopeTrackUuid == scope.ScopeTrackUuid
                    )
                    .Any(credit =>
                        outcomes.Any(outcome =>
                            outcome.RequestedCreditUuid == credit.Uuid
                            && outcome.State != "accepted"
                        )
                    )
            )
            {
                continue;
            }
            await c.ExecuteAsync(
                new CommandDefinition(
                    """
                    UPDATE normalization.recording_credit_contributor_support support SET retired_work_uuid=@workUuid
                    FROM normalization.recording_credits credit WHERE credit.uuid=support.credit_uuid
                      AND credit.source_uuid=@sourceUuid
                      AND support.contributor_provider=@Provider AND support.contributor_type=@ResourceType AND support.contributor_key=@ProviderKey
                      AND support.retired_work_uuid IS NULL AND NOT support.uuid=ANY(@kept)
                      AND credit.role=@Role AND credit.scope_track_uuid IS NOT DISTINCT FROM @ScopeTrackUuid::uuid
                    """,
                    new
                    {
                        workUuid,
                        sourceUuid,
                        replacement.Contributor.Provider,
                        replacement.Contributor.ResourceType,
                        replacement.Contributor.ProviderKey,
                        scope.Role,
                        scope.ScopeTrackUuid,
                        kept = kept.ToArray(),
                    },
                    t,
                    cancellationToken: ct
                )
            );
        }
        return outcomes;
    }

    private static async Task<RecordingCredit> HoldChangedReviewedField(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        RecordingCredit credit,
        CancellationToken ct
    )
    {
        foreach (var evidence in credit.Evidence)
        {
            var reviewed = await c.ExecuteScalarAsync<bool>(
                new CommandDefinition(
                    """
                    SELECT EXISTS(SELECT 1 FROM (
                            SELECT uuid,revision,source_uuid,role,scope_track_uuid,original_text,person_uuid
                            FROM normalization.recording_credits WHERE basis='reviewed'
                            UNION ALL
                            SELECT h.uuid,h.revision,h.source_uuid,h.role,h.scope_track_uuid,h.original_text,h.person_uuid
                            FROM normalization.recording_credit_history h
                            JOIN normalization.review_operations operation ON operation.uuid=h.superseding_decision_uuid
                        ) credit
                        JOIN normalization.recording_credit_evidence e ON e.credit_uuid=credit.uuid AND e.credit_revision=credit.revision
                        JOIN normalization.input_revisions old_input ON old_input.uuid=e.input_revision_uuid
                        JOIN normalization.input_revisions new_input ON new_input.uuid=@InputRevisionUuid AND new_input.resource_uuid=old_input.resource_uuid
                        WHERE credit.source_uuid=@SourceUuid AND credit.role=@Role AND credit.scope_track_uuid IS NOT DISTINCT FROM @ScopeTrackUuid::uuid
                            AND e.json_pointer=@JsonPointer
                            AND (old_input.uuid<>new_input.uuid OR (credit.original_text=@OriginalText
                                AND (e.start_code_point IS NULL OR @StartCodePoint::integer IS NULL
                                    OR (e.start_code_point<@EndCodePoint AND @StartCodePoint<e.end_code_point))))
                            AND (credit.original_text<>@OriginalText OR credit.person_uuid IS DISTINCT FROM @PersonUuid::uuid))
                    """,
                    new
                    {
                        credit.SourceUuid,
                        credit.Role,
                        credit.ScopeTrackUuid,
                        credit.OriginalText,
                        credit.PersonUuid,
                        evidence.InputRevisionUuid,
                        evidence.JsonPointer,
                        evidence.StartCodePoint,
                        evidence.EndCodePoint,
                    },
                    t,
                    cancellationToken: ct
                )
            );
            if (reviewed)
            {
                return credit with
                {
                    State = "pending",
                    ReasonCode = "changed_evidence_reviewed_claim",
                };
            }
        }
        return credit;
    }

    private static async Task RetireUnsupportedCredits(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid workUuid,
        Guid sourceUuid,
        CancellationToken ct
    )
    {
        var rows = await c.QueryAsync<CreditRow>(
            new CommandDefinition(
                $"""
                SELECT {CreditColumns} FROM normalization.recording_credits credit
                WHERE credit.source_uuid=@sourceUuid AND credit.basis<>'reviewed' AND credit.state IN ('accepted','pending')
                    AND EXISTS(SELECT 1 FROM normalization.recording_credit_managed managed WHERE managed.credit_uuid=credit.uuid)
                    AND NOT EXISTS(SELECT 1 FROM normalization.recording_credit_unmanaged_support baseline WHERE baseline.credit_uuid=credit.uuid)
                    AND NOT EXISTS(SELECT 1 FROM normalization.recording_credit_contributor_support support WHERE support.credit_uuid=credit.uuid AND support.retired_work_uuid IS NULL)
                ORDER BY credit.uuid FOR UPDATE
                """,
                new { sourceUuid },
                t,
                cancellationToken: ct
            )
        );
        foreach (var row in rows)
        {
            var evidence = await c.QueryAsync<NormalizedEvidenceReference>(
                new CommandDefinition(
                    """
                    SELECT input_revision_uuid AS "InputRevisionUuid",json_pointer AS "JsonPointer",start_code_point AS "StartCodePoint",end_code_point AS "EndCodePoint",quote AS "Quote"
                    FROM normalization.recording_credit_evidence WHERE credit_uuid=@Uuid AND credit_revision=@Revision
                    """,
                    row,
                    t,
                    cancellationToken: ct
                )
            );
            await StoreCreditRevision(
                c,
                t,
                new(
                    row.Uuid,
                    row.SourceUuid,
                    row.Role,
                    row.OriginalText,
                    row.PersonUuid,
                    row.ScopeTrackUuid,
                    evidence.ToArray(),
                    "superseded",
                    row.Basis,
                    "contributor_claim_removed"
                ),
                row,
                workUuid,
                false,
                ct
            );
        }
    }
}
