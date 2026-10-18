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
    private async Task<NextStage> PrepareNextStage(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SourceActorAliasApplication application,
        SourceActorResolutionBatch proposal,
        CancellationToken ct
    )
    {
        // Reuse only the current person's map within this RR preparation. Retaining the
        // whole inventory would multiply each independently bounded projection in memory.
        (Guid Person, PersonMap Map)? cachedMap = null;
        async Task<PersonMap> MapFor(Guid person)
        {
            if (cachedMap is { } previous && previous.Person == person)
            {
                return previous.Map;
            }
            var map = await ReadPersonMap(c, t, person, ct);
            cachedMap = (person, map);
            return map;
        }

        // Original evidence and all current source review decisions are authenticated before
        // any positive work. Both trusted and machine phases use this same RR preparation.
        var committed = new SourceActorAliasApplicationIndex(application.Stages);
        var source = await ReadTrustedSourceAsync(c, t, proposal, ct, committed);
        Guid[]? oldPeople = null;
        var scanHistoricalReviews =
            source.Reviews.OwnerObservation == null
            || await c.QuerySingleAsync<bool>(
                new CommandDefinition(
                    "SELECT EXISTS(SELECT 1 FROM normalization.source_actor_alias_review_state WHERE source_uuid=@SourceUuid)",
                    new { application.SourceUuid },
                    t,
                    cancellationToken: ct
                )
            );
        // Field-local absence cannot rule out a review on removed historical text. Only
        // source-wide absence, fenced by the actual source owner, skips withdrawal discovery.
        // Unrelated old-map corruption may then be refused after an independent addition.
        if (scanHistoricalReviews)
        {
            oldPeople = await ReadPreviouslyPublishedPeople(c, t, application.SourceUuid, ct);
            foreach (var person in oldPeople)
            {
                var map = await MapFor(person);
                var enriched = await IncludeHistoricalReviews(
                    c,
                    t,
                    source,
                    map.Map,
                    application.SourceUuid,
                    ct
                );
                var withdrawal = await Withdrawals(c, t, enriched, map.Map, person, ct);
                if (withdrawal.Count > 0)
                {
                    return await PreparePersonStage(
                        c,
                        t,
                        application,
                        enriched,
                        person,
                        map,
                        withdrawal,
                        [],
                        ct
                    );
                }
            }
        }
        if (!source.Current)
        {
            // Historical human negatives above can still retire their exact disproved
            // authority. Positive additions and complete-field finalization require the
            // original source to remain current, regardless of earlier committed names.
            return new(
                null,
                SourceActorAliasApplicationDisposition.Stale,
                "source_proof_is_historical",
                []
            );
        }
        var trusted = UnwrittenClaims(committed, source)
            .FirstOrDefault(value => value.OriginalPersonUuid.HasValue);
        if (trusted != null)
        {
            var person = trusted.OriginalPersonUuid!.Value;
            var map = await MapFor(person);
            var deltas = await Additions(c, t, application, source, committed, person, ct);
            return await PreparePersonStage(c, t, application, source, person, map, deltas, [], ct);
        }
        var machine = await new MachineCohortRefresh(
            candidates,
            policies,
            decisions,
            proposal,
            source
        ).ReadAsync(c, t, committed, ct);
        switch (machine)
        {
            case SelectedMachinePerson selected:
                source = selected.Source;
                var person = selected.PersonUuid;
                var map = await MapFor(person);
                var deltas = await Additions(c, t, application, source, committed, person, ct);
                return await PreparePersonStage(
                    c,
                    t,
                    application,
                    source,
                    person,
                    map,
                    deltas,
                    [],
                    ct
                );
            case ExhaustedMachineClaims exhausted:
                source = exhausted.Source;
                break;
            default:
                throw new InvalidOperationException("Unsupported machine preparation result.");
        }

        // An incomplete cohort never reaches this phase. Current exhaustion still cannot
        // complete the application without checking every previous person's complete map.
        oldPeople ??= await ReadPreviouslyPublishedPeople(c, t, application.SourceUuid, ct);
        var readiness = await BuildReadiness(c, t, application, source, ct);
        foreach (var person in oldPeople)
        {
            var map = await MapFor(person);
            var finalization = Finalizations(application, source, person, map.Map, readiness);
            if (finalization.Count > 0)
            {
                return await PreparePersonStage(
                    c,
                    t,
                    application,
                    source,
                    person,
                    map,
                    finalization,
                    readiness,
                    ct
                );
            }
        }
        foreach (var person in oldPeople)
        {
            var map = await MapFor(person);
            var observations = IncompleteObservations(application, source, person, map.Map);
            if (observations.Count > 0)
            {
                return await PreparePersonStage(
                    c,
                    t,
                    application,
                    source,
                    person,
                    map,
                    observations,
                    readiness,
                    ct
                );
            }
        }
        return ClassifyApplicationOutcome(application, source, readiness);
    }

    private static async Task<Guid[]> ReadPreviouslyPublishedPeople(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid sourceUuid,
        CancellationToken ct
    )
    {
        var oldPeople = (
            await c.QueryAsync<Guid>(
                new CommandDefinition(
                    """
                    SELECT DISTINCT stage.person_uuid FROM normalization.source_actor_alias_person_stages stage
                    JOIN normalization.source_actor_alias_applications application ON application.attempt_uuid=stage.application_uuid
                    WHERE application.source_uuid=@source AND stage.receipt IS NOT NULL
                    ORDER BY stage.person_uuid LIMIT @take
                    """,
                    new { source = sourceUuid, take = 100001 },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        if (oldPeople.Length > 100000)
        {
            throw new SourceActorReviewCapacityException(
                "source_alias_complete_person_inventory_capacity_exceeded"
            );
        }
        return oldPeople;
    }

    private static NextStage ClassifyApplicationOutcome(
        SourceActorAliasApplication application,
        PreparedSource source,
        IReadOnlyList<SourceActorAliasReplacementReadiness> readiness
    )
    {
        var allOutcomes = application
            .Stages.Where(stage => stage.Receipt != null)
            .SelectMany(stage => stage.Receipt!.Names)
            .ToArray();
        var heldCompleteField = source
            .Inputs.SelectMany(
                (input, inputIndex) =>
                    input.Scopes.SelectMany(
                        (scope, scopeIndex) =>
                            scope
                                .Fields.Where(field =>
                                    scope.OriginalScope.Extraction.Scopes.Any(coverage =>
                                        coverage.Field == field.Field && coverage.Complete
                                    )
                                )
                                .Select(field =>
                                    (inputIndex, scopeIndex, Pointer: field.Evidence.JsonPointer)
                                )
                    )
            )
            .Any(field =>
                !readiness.Any(value =>
                    value.CompleteObservation.InputIndex == field.inputIndex
                    && value.CompleteObservation.ScopeIndex == field.scopeIndex
                    && value.CompleteObservation.FieldJsonPointer == field.Pointer
                )
            );
        var blocked =
            heldCompleteField
            || allOutcomes.Any(name =>
                name.Disposition
                    is not (
                        SourceActorAliasNameDisposition.Accepted
                        or SourceActorAliasNameDisposition.ExactDuplicate
                    )
            )
            || application
                .Stages.Where(stage => stage.Receipt != null)
                .Any(stage =>
                    stage.Receipt!.Names.Any(name =>
                        source.Claims.Single(claim => claim.Claim == name.Claim).OriginalPersonUuid
                        != stage.OriginalPersonUuid
                    )
                );
        // NULL fields have no observed text and cannot clear old authority. Existing map
        // scopes for them already receive durable incomplete observations above. They do
        // not make an unrelated, complete positive field incomplete; an entirely absent
        // field inventory still cannot produce an imaginary complete source scope.
        var unresolved =
            source.Claims.Any(claim => !claim.OriginalPersonUuid.HasValue)
            || application.Stages.Any(stage =>
                stage.Receipt?.Reason == "incomplete_source_observation_preserved_support"
            )
            || source.Inputs.Any(input =>
                !input.AliasPayloadPresent
                || input.DecodeIssues.Any(RecordingActorDecodeIssues.AffectsActorEvidence)
                || input.Scopes.Count == 0
                || input.Scopes.All(scope => scope.Fields.Count == 0)
                || input.Scopes.Any(scope =>
                    scope.Fields.Any(field =>
                        !scope
                            .OriginalScope.Extraction.Scopes.Single(coverage =>
                                coverage.Field == field.Field
                            )
                            .Complete
                    )
                )
            );
        var disposition =
            blocked ? SourceActorAliasApplicationDisposition.Review
            : unresolved
                ? (
                    application.Stages.Any(stage => stage.Receipt != null)
                        ? SourceActorAliasApplicationDisposition.Partial
                        : SourceActorAliasApplicationDisposition.Unresolved
                )
            : SourceActorAliasApplicationDisposition.Complete;
        if (
            source.Current
            && application.Stages.Count == 0
            && disposition == SourceActorAliasApplicationDisposition.Complete
        )
        {
            // No person stage means no accepted scope receipt exists. Retain a negative
            // application audit, without claiming a current clear on an imaginary owner.
            return new(
                null,
                SourceActorAliasApplicationDisposition.Unresolved,
                "no_affected_person_scope",
                readiness
            );
        }
        return new(
            null,
            disposition,
            blocked ? "canonical_alias_review_preserved_previous_support"
                : unresolved ? "incomplete_source_scope_preserved_previous_support"
                : "source_alias_scopes_applied",
            readiness
        );
    }

    internal static IReadOnlyList<SourceActorAliasStageDelta> IncompleteObservations(
        SourceActorAliasApplication application,
        PreparedSource source,
        Guid person,
        IReadOnlyList<SourceActorAliasScopeMapEntry> map
    )
    {
        var result = new List<SourceActorAliasStageDelta>();
        if (!source.Current)
        {
            return result;
        }
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
            var scope = scopeIndex < 0 ? null : input.Scopes[scopeIndex];
            var field = scope?.Fields.SingleOrDefault(value =>
                value.Evidence.JsonPointer == entry.Scope.FieldJsonPointer
            );
            var coverage =
                field == null
                    ? null
                    : scope!.OriginalScope.Extraction.Scopes.Single(value =>
                        value.Field == field.Field
                    );
            if (
                input.AliasPayloadPresent
                && !input.DecodeIssues.Any(RecordingActorDecodeIssues.AffectsActorEvidence)
                && coverage?.Complete == true
            )
            {
                continue;
            }
            var observation = new SourceActorAliasScopeObservation(
                application.Plan!.Proposal,
                inputIndex,
                scopeIndex < 0 ? null : scopeIndex,
                entry.Scope.FieldJsonPointer,
                false,
                false
            );
            if (entry.IncompleteObservations.Contains(observation))
            {
                continue;
            }
            result.Add(
                new(
                    SourceActorAliasDeltaKind.ObserveIncomplete,
                    entry.Scope,
                    person,
                    null,
                    null,
                    null,
                    [],
                    [],
                    [],
                    observation,
                    null
                )
            );
        }
        return result;
    }

    private static IEnumerable<SourceActorAliasPlannedClaim> UnwrittenClaims(
        SourceActorAliasApplicationIndex committed,
        PreparedSource source
    ) => source.Claims.Where(claim => !committed.Claims.ContainsKey(claim.Claim));

    private async Task<IReadOnlyList<SourceActorAliasStageDelta>> Additions(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SourceActorAliasApplication application,
        PreparedSource source,
        SourceActorAliasApplicationIndex committed,
        Guid person,
        CancellationToken ct
    )
    {
        var stage = StableUuid(application.AttemptUuid, "stage/" + application.Stages.Count);
        var result = new List<SourceActorAliasStageDelta>();
        foreach (
            var planned in UnwrittenClaims(committed, source)
                .Where(value => value.OriginalPersonUuid == person)
        )
        {
            var claim = Claim(source, planned.Claim);
            var input = source.Inputs[planned.Claim.InputIndex];
            var scope = input.Scopes[planned.Claim.ScopeIndex];
            var assertion = scope.OriginalScope.Extraction.Assertions[planned.Claim.AssertionIndex];
            var review = source.Reviews.Reviews.SingleOrDefault(value =>
                value.Coordinates == claim.Coordinates
            );
            var correspondence = (source.Reviews.Correspondences ?? [])
                .Where(value => value.Current.Coordinates == claim.Coordinates)
                .OrderBy(value => value.OriginalReview.Uuid)
                .FirstOrDefault();
            var accepted = review?.State == "accepted" ? review : correspondence?.OriginalReview;
            var human =
                accepted == null
                    ? null
                    : (
                        await SourceActorReviewedEvidence.ReadReviewPreparationAsync(
                            c,
                            t,
                            accepted,
                            ct
                        )
                        ?? throw new NormalizationConflictException(
                            "A selected source review lacks its immutable trusted receipt."
                        )
                    );
            var authority = new SourceActorAliasAcceptanceReference(
                planned.Claim,
                accepted == null ? SourceActorAliasAuthorityKind.Machine
                    : correspondence == null ? SourceActorAliasAuthorityKind.Reviewed
                    : SourceActorAliasAuthorityKind.Correspondence,
                human?.PreparationUuid,
                accepted?.Uuid,
                accepted?.ReviewRevision ?? 0,
                accepted?.LastOperationUuid,
                person
            );
            var bound = await c.QuerySingleOrDefaultAsync<Guid?>(
                new CommandDefinition(
                    """
                    SELECT person_uuid FROM normalization.source_actor_alias_assignments WHERE retired_work_uuid IS NULL
                        AND source_uuid=@SourceUuid AND resource_uuid=@ResourceUuid AND input_revision_uuid=@InputRevisionUuid
                        AND field_json_pointer=@FieldJsonPointer AND producer_version=@ProducerVersion AND assertion_uuid=@AssertionUuid
                        AND contributor_provider=@Provider AND contributor_type=@ResourceType AND contributor_key=@ProviderKey
                    """,
                    new
                    {
                        claim.Coordinates.SourceUuid,
                        claim.Coordinates.ResourceUuid,
                        claim.Coordinates.InputRevisionUuid,
                        claim.Coordinates.FieldJsonPointer,
                        claim.Coordinates.ProducerVersion,
                        claim.Coordinates.AssertionUuid,
                        claim.Coordinates.Contributor.Provider,
                        claim.Coordinates.Contributor.ResourceType,
                        claim.Coordinates.Contributor.ProviderKey,
                    },
                    t,
                    cancellationToken: ct
                )
            );
            if (bound.HasValue && bound != person)
            {
                throw new NormalizationConflictException(
                    "Existing exact source binding requires explicit reviewed reassignment before another person is linked."
                );
            }
            result.Add(
                new(
                    SourceActorAliasDeltaKind.Add,
                    ScopeKey(
                        application.SourceUuid,
                        input,
                        scope,
                        claim.Coordinates.FieldJsonPointer
                    ),
                    person,
                    claim.Coordinates,
                    claim.AssertionSha256,
                    authority,
                    Names(stage, planned.Claim, assertion),
                    [],
                    [],
                    null,
                    null
                )
            );
        }
        return result;
    }

    private async Task<PreparedSource> IncludeHistoricalReviews(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        PreparedSource source,
        IReadOnlyList<SourceActorAliasScopeMapEntry> map,
        Guid sourceUuid,
        CancellationToken ct
    )
    {
        var reviews = source.Reviews.Reviews.ToList();
        var history = map.Where(entry => entry.Scope.SourceUuid == sourceUuid)
            .SelectMany(entry =>
                entry
                    .Bindings.Select(binding =>
                        binding.Acceptance.Claim.Proposal.ExtractionAttemptUuid
                    )
                    .Concat(
                        (entry.LastWithdrawalAuthorities ?? []).Select(value =>
                            value.Claim.Proposal.ExtractionAttemptUuid
                        )
                    )
            )
            .Distinct();
        foreach (var extractionUuid in history.Where(uuid => uuid != source.Extraction.AttemptUuid))
        {
            var extraction = await SourceActorResolutionStore.ReadPublishedExtractionAsync(
                c,
                t,
                extractionUuid,
                sourceUuid,
                ct
            );
            var inputs = await SourceActorResolutionStore.ReadOriginalInputsForAcceptedProofAsync(
                c,
                t,
                extraction,
                ct
            );
            var old = await SourceActorResolutionStore.ReadSourceReviewsAsync(
                c,
                t,
                extraction,
                inputs,
                ct
            );
            reviews.AddRange(old.Reviews);
        }
        return source with
        {
            Reviews = source.Reviews with { Reviews = Coherent(reviews, value => value.Uuid) },
        };
    }

    private static async Task<IReadOnlyList<SourceActorAliasStageDelta>> Withdrawals(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        PreparedSource source,
        IReadOnlyList<SourceActorAliasScopeMapEntry> map,
        Guid person,
        CancellationToken ct
    )
    {
        // The caller includes reviews for every retained original field before this phase.
        // An empty complete inventory cannot authorize withdrawal. Keep its absence fence
        // in the new stage, without rereading each assignment's whole historical proposal.
        if (source.Reviews.Reviews.Count == 0)
        {
            return [];
        }

        var result = new List<SourceActorAliasStageDelta>();
        foreach (
            var entry in map.Where(entry => entry.Scope.SourceUuid == source.Extraction.SourceUuid)
        )
        {
            foreach (var group in entry.Bindings.GroupBy(binding => binding.AssignmentUuid))
            {
                var acceptance = group.First().Acceptance;
                var exact = acceptance.ReviewStateUuid is Guid reviewed
                    ? source.Reviews.Reviews.SingleOrDefault(value => value.Uuid == reviewed)
                    : null;
                if (exact == null)
                {
                    var historical = await ReadAcceptedClaimForWithdrawal(
                        c,
                        t,
                        acceptance.Claim,
                        ct
                    );
                    exact = source.Reviews.Reviews.SingleOrDefault(value =>
                        value.Coordinates == historical.Claim.Coordinates
                    );
                }
                if (
                    exact == null
                    || exact.State == "accepted"
                        && (
                            exact.SelectedPersonUuid == person
                            || exact.Coordinates.ProducerVersion
                                != RetainedSourceActorAliasExtractor.Version
                        )
                )
                {
                    continue;
                }
                var human =
                    await SourceActorReviewedEvidence.ReadReviewPreparationAsync(c, t, exact, ct)
                    ?? throw new NormalizationConflictException(
                        "Source withdrawal requires an actual trusted review operation."
                    );
                var original = await SourceActorResolutionStore.ReadPublishedExtractionAsync(
                    c,
                    t,
                    human.Request.ExtractionAttemptUuid,
                    source.Extraction.SourceUuid,
                    ct
                );
                var inputs =
                    await SourceActorResolutionStore.ReadOriginalInputsForAcceptedProofAsync(
                        c,
                        t,
                        original,
                        ct
                    );
                if (
                    !SourceActorReviewEvidence.Same(
                        SourceActorReviewEvidence.SelectField(original, inputs, exact.Coordinates),
                        human.OriginalField
                    )
                )
                {
                    throw new NormalizationConflictException(
                        "Historical withdrawal does not authenticate its original source field."
                    );
                }
                result.Add(
                    new(
                        SourceActorAliasDeltaKind.Retire,
                        entry.Scope,
                        person,
                        exact.Coordinates,
                        exact.AssertionSha256,
                        acceptance with
                        {
                            Kind = SourceActorAliasAuthorityKind.Withdrawal,
                            ReviewPreparationUuid = human.PreparationUuid,
                            ReviewStateUuid = exact.Uuid,
                            ReviewRevision = exact.ReviewRevision,
                            ReviewOperationUuid = exact.LastOperationUuid,
                            OriginalSelectedPersonUuid = exact.SelectedPersonUuid,
                        },
                        [],
                        [group.Key],
                        group.Select(binding => binding.SupportUuid).ToArray(),
                        null,
                        null
                    )
                );
            }
        }
        return result;
    }

    // This reads an already accepted binding's immutable interpretation. It grants no Add
    // authority: callers must also prove the actual prior map and a current negative review.
    internal static async Task<OriginalClaim> ReadAcceptedClaimForWithdrawal(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SourceActorAliasClaimReference reference,
        CancellationToken ct
    )
    {
        var batch = await ReadPublishedProposal(c, t, reference.Proposal, null, true, ct);
        var input = batch.Preparation.Inputs[reference.InputIndex];
        var scope = input.Scopes[reference.ScopeIndex];
        return new(
            batch,
            input,
            scope,
            scope.Claims.Single(value => value.AssertionIndex == reference.AssertionIndex)
        );
    }

    internal static Task<SourceActorResolutionBatch> ReadOriginalProposal(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SourceActorAliasProposalReference reference,
        Guid? expectedSourceUuid,
        CancellationToken ct
    ) => ReadPublishedProposal(c, t, reference, expectedSourceUuid, false, ct);

    private static async Task<SourceActorResolutionBatch> ReadPublishedProposal(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SourceActorAliasProposalReference reference,
        Guid? expectedSourceUuid,
        bool historicalAcceptedProof,
        CancellationToken ct
    )
    {
        var json =
            await c.QuerySingleOrDefaultAsync<string>(
                new CommandDefinition(
                    "SELECT batch::text FROM normalization.source_actor_resolution_attempts WHERE attempt_uuid=@uuid AND disposition='published'",
                    new { uuid = reference.ResolutionAttemptUuid },
                    t,
                    cancellationToken: ct
                )
            )
            ?? throw new NormalizationConflictException(
                "Original accepted source proposal history is missing."
            );
        var batch = SourceActorResolutionCheckpoint.ReadBatch(json);
        if (
            batch.AttemptUuid != reference.ResolutionAttemptUuid
            || batch.ExtractionAttemptUuid != reference.ExtractionAttemptUuid
            || batch.ProcessingVersion != reference.ProcessingVersion
            || (
                batch.ProcessingVersion != SourceActorResolutionService.Version
                && !(
                    historicalAcceptedProof
                    && SupportsHistoricalAcceptedProposal(batch.ProcessingVersion)
                )
            )
            || expectedSourceUuid.HasValue && batch.SourceUuid != expectedSourceUuid
            || batch.Ticket.WorkUuid != batch.AttemptUuid
            || batch.Ticket.PolicyVersion != batch.ProcessingVersion
            || !SourceActorResolutionReadSet.IsComplete(batch.Ticket, batch.Preparation)
        )
        {
            throw new NormalizationConflictException(
                "Source reference differs from its actual immutable published proposal receipt."
            );
        }

        // The saved batch and ticket identify the actual original contributed admission.
        // Historical proof cannot be borrowed from ordinary Work with coincidentally equal facts.
        var requestHash = await c.QuerySingleOrDefaultAsync<string>(
            new CommandDefinition(
                """
                SELECT run.request_hash FROM normalization.work_runs run
                    JOIN normalization.work_subjects owner ON owner.uuid=run.subject_uuid
                    WHERE run.uuid=@WorkUuid AND run.state='published' AND run.ticket=@ticket::jsonb
                        AND owner.uuid=@SubjectUuid AND owner.domain='people'
                        AND owner.subject_kind='source_actor_resolution' AND owner.subject_uuid=@SourceUuid
                """,
                new
                {
                    batch.Ticket.WorkUuid,
                    batch.Ticket.SubjectUuid,
                    batch.SourceUuid,
                    ticket = System.Text.Json.JsonSerializer.Serialize(batch.Ticket),
                },
                t,
                cancellationToken: ct
            )
        );
        if (
            requestHash == null
            || !NormalizationWorkService.MatchesContributedBatchRequest(
                requestHash,
                SourceActorResolutionService.Owner(batch.SourceUuid),
                batch.ProcessingVersion,
                SourceActorResolutionReadSet.Contributions(batch.Preparation),
                batch.Preparation.ExpectedConfigurationGeneration,
                batch.Preparation.ExpectedContributions
            )
        )
        {
            throw new NormalizationConflictException(
                "Source reference lacks its exact original contributed publication."
            );
        }
        return batch;
    }

    private const string ResolutionPrefix = "source-actor-resolution-proposals-v1/";
    private const string HistoricalResolutionComponents =
        "/people-candidate-facts-v5/people-actor-retrieval-v3/people-actor-projection-v1/"
        + "people-actor-context-linear-v3/people-actor-projection-v1/people-actor-context-features-v1/"
        + "source-actor-review-correspondence-v1/source-actor-resolution-decisions-v1/"
        + "people-source-actor-alias-projection-v1/";
    private const string ResolutionFeatureSuffix = "/people-actor-context-features-v1";
    private const string HistoricalResolutionSuffix =
        HistoricalResolutionComponents
        + SourceActorAliasExtractor.LegacyV2Version
        + ResolutionFeatureSuffix;
    private const string HistoricalV6Resolution =
        ResolutionPrefix
        + SourceActorResolutionStore.LegacyAliasV2DecoderV6ProcessingVersion
        + HistoricalResolutionSuffix;
    private const string HistoricalV7Resolution =
        ResolutionPrefix
        + SourceActorResolutionStore.LegacyAliasV2DecoderV7ProcessingVersion
        + HistoricalResolutionSuffix;
    private const string ApplicationPrefix = "source-actor-alias-publication-v1/";
    private const string ApplicationSuffix = "/complete-person-map-v1/component-review-v1";

    private static bool SupportsHistoricalAcceptedProposal(string version) =>
        CurrentResolutionComponentsMatch()
        && (version == HistoricalV6Resolution || version == HistoricalV7Resolution);

    internal static bool SupportsHistoricalAcceptedApplication(string version) =>
        CurrentResolutionComponentsMatch()
        && (
            version == ApplicationPrefix + HistoricalV6Resolution + ApplicationSuffix
            || version == ApplicationPrefix + HistoricalV7Resolution + ApplicationSuffix
        );

    private static bool CurrentResolutionComponentsMatch() =>
        // Preserve every non-alias component of the known profiles. The original-source
        // reader separately requires the exact known decoder and full legacy field replay.
        SourceActorResolutionService.Version
        == ResolutionPrefix
            + NormalizationRecordingProcessor.Version
            + HistoricalResolutionComponents
            + SourceActorAliasExtractor.Version
            + ResolutionFeatureSuffix;

    internal sealed record OriginalClaim(
        SourceActorResolutionBatch Batch,
        SourceActorResolutionInput Input,
        SourceActorResolutionScope Scope,
        SourceActorResolutionClaim Claim
    );
}
