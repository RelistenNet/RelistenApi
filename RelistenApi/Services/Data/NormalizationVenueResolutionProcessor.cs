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
/// Advances one saved venue resolution phase per call. Reusing an attempt UUID resumes it;
/// RetryAsync uses a new UUID and rereads the source. No accepted target is supplied by callers.
/// </summary>
public sealed partial class NormalizationVenueResolutionProcessor(
    DbService db,
    NormalizationCandidateService candidates,
    NormalizationImportedCatalogService imported,
    NormalizationCatalogBindingService bindings,
    NormalizationEvidenceService evidence,
    NormalizationProviderDecoder decoder,
    VenueResolutionPolicyService policies,
    NormalizationVenueResolutionStore store,
    NormalizationWorkService work,
    NormalizedVenueService venues,
    NormalizationVenueSourceService sources
)
{
    public const string Version = VenueResolutionPolicyEvaluator.ProcessorVersion;

    public Task<VenueResolutionAttempt> ResolveImportedVenueAsync(
        Guid attemptUuid,
        Guid importedVenueUuid,
        CancellationToken ct = default
    ) =>
        StartAsync(
            attemptUuid,
            new(VenueResolutionRequestKind.ImportedVenue, importedVenueUuid),
            null,
            ct
        );

    public Task<VenueResolutionAttempt> ResolveShowAsync(
        Guid attemptUuid,
        VenueShowClaimReference reference,
        CancellationToken ct = default
    ) =>
        StartAsync(
            attemptUuid,
            new(VenueResolutionRequestKind.ShowObservation, Show: reference),
            null,
            ct
        );

    /// <summary>Use exact retained provider evidence; callers cannot supply qualification flags.</summary>
    public Task<VenueResolutionAttempt> ResolveImportedVenueAsync(
        Guid attemptUuid,
        Guid importedVenueUuid,
        IReadOnlyList<VenueResolutionSupportReference> support,
        CancellationToken ct = default
    ) =>
        StartAsync(
            attemptUuid,
            new(
                VenueResolutionRequestKind.ImportedVenue,
                importedVenueUuid,
                Support: support.ToArray()
            ),
            null,
            ct
        );

    /// <summary>Enrich the original published account without replacing its source coordinates.</summary>
    public Task<VenueResolutionAttempt> ResolveShowAsync(
        Guid attemptUuid,
        VenueShowClaimReference reference,
        IReadOnlyList<VenueResolutionSupportReference> support,
        CancellationToken ct = default
    ) =>
        StartAsync(
            attemptUuid,
            new(
                VenueResolutionRequestKind.ShowObservation,
                Show: reference,
                Support: support.ToArray()
            ),
            null,
            ct
        );

    public Task<VenueResolutionAttempt?> GetAsync(
        Guid attemptUuid,
        CancellationToken ct = default
    ) => store.GetAsync(attemptUuid, ct);

    public async Task<VenueResolutionAttempt> RetryAsync(
        Guid newAttemptUuid,
        Guid previousAttemptUuid,
        CancellationToken ct = default
    )
    {
        if (newAttemptUuid == previousAttemptUuid)
        {
            throw new ArgumentException("A venue retry requires a new attempt UUID.");
        }
        var previous =
            await store.GetAsync(previousAttemptUuid, ct)
            ?? throw new ArgumentException("Unknown venue resolution attempt.");
        // Show retries preserve the exact account coordinate. A newer published account must be
        // named explicitly; a retry cannot quietly substitute another source interpretation.
        return await StartAsync(newAttemptUuid, previous.Request, previousAttemptUuid, ct);
    }

    private async Task<VenueResolutionAttempt> StartAsync(
        Guid uuid,
        VenueResolutionRequest request,
        Guid? previous,
        CancellationToken ct
    )
    {
        var attempt = await store.GetAsync(uuid, ct);
        if (attempt != null && !NormalizationVenueResolutionStore.Same(attempt.Request, request))
        {
            throw new NormalizationConflictException(
                "The venue attempt belongs to another source claim."
            );
        }
        if (attempt != null && previous != null && attempt.PreviousAttemptUuid != previous)
        {
            throw new NormalizationConflictException(
                "A venue retry cannot change its original attempt lineage."
            );
        }
        attempt ??= await store.GetOrCreateAsync(uuid, request, Version, previous, ct);
        return await AdvanceAsync(attempt, ct);
    }

    private async Task<VenueResolutionAttempt> AdvanceAsync(
        VenueResolutionAttempt attempt,
        CancellationToken ct
    )
    {
        if (attempt.Result != null)
        {
            return attempt with { Result = attempt.Result with { Replayed = true } };
        }
        if (attempt.ProcessingVersion != Version)
        {
            return await store.CompleteAsync(
                attempt,
                VenueResolutionDisposition.Unresolved,
                "processing_version_changed",
                ct
            );
        }
        try
        {
            if (attempt.Preparation == null)
            {
                return await PrepareAsync(attempt, ct);
            }
            var preparation = attempt.Preparation;
            var pending = attempt.Stages.FirstOrDefault(stage => stage.Receipt == null);
            if (pending != null)
            {
                return await AdvanceStageAsync(attempt, pending, ct);
            }
            if (
                attempt.Stages.LastOrDefault() is { } completed
                && completed.Kind != VenueResolutionStageKind.CreateVenue
            )
            {
                if (
                    preparation.Context.ObservationSubscription != null
                    && completed.Kind != VenueResolutionStageKind.SupersedeObservation
                )
                {
                    return await PrepareObservationRetirementAsync(attempt, ct);
                }
                if (completed.Kind == VenueResolutionStageKind.SupersedeObservation)
                {
                    // Retirement is subscription cleanup; the bound stage's preserved
                    // accepted/review outcome remains the result of this resolution.
                    completed = attempt.Stages.Single(stage =>
                        stage.Kind
                            is VenueResolutionStageKind.Assignment
                                or VenueResolutionStageKind.AcknowledgeDecision
                    );
                }
                if (completed.Kind == VenueResolutionStageKind.AcknowledgeDecision)
                {
                    var reason =
                        preparation.Status == VenueResolutionPreparationStatus.SourceUnbound
                            ? "source_performance_unbound"
                        : preparation.Status == VenueResolutionPreparationStatus.UnsupportedQuery
                            ? "source_query_unsupported"
                        : attempt.Decision!.Decision.Reason;
                    var unresolved =
                        preparation.Status != VenueResolutionPreparationStatus.Ready
                        || preparation.Policy == null;
                    return await store.CompleteAsync(
                        attempt,
                        unresolved
                            ? VenueResolutionDisposition.Unresolved
                            : VenueResolutionDisposition.Review,
                        reason,
                        ct
                    );
                }
                var missing = completed.Kind == VenueResolutionStageKind.AcknowledgeMissing;
                var applied = completed.Outcome?.Applied == true;
                return await store.CompleteAsync(
                    attempt,
                    missing ? VenueResolutionDisposition.Missing
                        : applied ? VenueResolutionDisposition.Applied
                        : VenueResolutionDisposition.Review,
                    missing ? "imported_venue_missing"
                        : applied ? "evaluated_venue_correspondence"
                        : "assignment_authority_preserved",
                    ct
                );
            }
            if (preparation.Source.Absence is { Exists: false })
            {
                return await PrepareMissingStageAsync(attempt, ct);
            }
            if (!preparation.Context.SourceCurrent)
            {
                return await store.CompleteAsync(
                    attempt,
                    VenueResolutionDisposition.Stale,
                    "source_observation_changed",
                    ct
                );
            }
            if (
                preparation.Context.Candidates.Issues.Any(issue =>
                    issue.Kind
                        is CandidateFactsIssueKind.MissingOwner
                            or CandidateFactsIssueKind.MissingCatalogClock
                    || issue.FactKind is "owner_binding_stamp_missing" or "owner_input_head_missing"
                )
            )
            {
                // Unknown geography and bounded facts can be watched through the candidate
                // epoch. A missing freshness stamp cannot: repairing only a historical owner
                // need not change an accepted row. Do not invent a placeholder observation.
                return await store.CompleteAsync(
                    attempt,
                    VenueResolutionDisposition.Stale,
                    "context_freshness_unavailable",
                    ct
                );
            }
            if (preparation.Status != VenueResolutionPreparationStatus.Ready)
            {
                return await PrepareNegativeStageAsync(attempt, ct);
            }
            if (attempt.Decision == null)
            {
                return await EvaluateAsync(attempt, ct);
            }
            return await PrepareStageAsync(attempt, ct);
        }
        catch (NormalizationConflictException)
        {
            // The store's exact attempt CAS prevents a losing invocation from terminalizing a
            // later checkpoint written by another worker.
            return await store.CompleteAsync(
                attempt,
                VenueResolutionDisposition.Stale,
                "venue_read_set_changed",
                ct
            );
        }
    }

    private Task<VenueResolutionAttempt> PrepareAsync(
        VenueResolutionAttempt attempt,
        CancellationToken ct
    ) =>
        attempt.Request.Kind == VenueResolutionRequestKind.ImportedVenue
            ? PrepareImportedAsync(attempt, ct)
            : PrepareShowAsync(attempt, ct);

    private async Task<VenueResolutionAttempt> PrepareImportedAsync(
        VenueResolutionAttempt attempt,
        CancellationToken ct
    )
    {
        var uuid = attempt.Request.ImportedVenueUuid!.Value;
        var probe = await candidates.ReadMissingImportedVenueResolverContextAsync(uuid, ct);
        if (!probe.Probe.Exists)
        {
            var absent = new VenueResolutionSource(null, null, null, Absence: probe.Probe);
            var read = SourceDecisionReadSet(absent, probe.Context, null, []);
            return await store.SavePreparationAsync(
                attempt.AttemptUuid,
                new(absent, probe.Context, read, null),
                ct
            );
        }
        var snapshot = await imported.RefreshImportedVenueAsync(uuid, ct);
        if (snapshot == null)
        {
            // No fresh stamp is attached to an earlier null refresh. The next invocation
            // repeats the whole preparation and obtains its own atomic absence probe.
            return attempt;
        }
        var binding = await bindings.BindRefreshedImportedVenueAsync(snapshot, ct);
        if (binding.Venue.Value == null)
        {
            throw new NormalizationConflictException("The imported venue binding changed.");
        }
        var decoded = decoder.Decode(snapshot.Capture.Revision, ct);
        if (decoded.Issues.Count != 0 || decoded.Venues.Count != 1)
        {
            return await store.CompleteAsync(
                attempt,
                VenueResolutionDisposition.Unresolved,
                "unsupported_imported_venue_shape",
                ct
            );
        }
        var observation = decoded.Venues.Single();
        var source = new VenueResolutionSource(
            new(
                uuid,
                false,
                observation,
                observation.Origin.Evidence,
                binding.Venue.Value.ArtistUuid
            ),
            null,
            null,
            ImportedBinding: binding
        );
        var identity = (
            await ReadVenueInputIdentitiesAsync([binding.InputRevisionUuid], ct)
        ).Single();
        var input = new NormalizationInputDependency(
            identity.ResourceUuid,
            binding.InputRevisionUuid,
            snapshot.Capture.Fetch.ResourceGeneration
        );
        var query = Query(observation);
        var context = !SupportsQuery(query)
            ? await candidates.ReadUnsupportedVenueResolverContextAsync(source, ct)
            : await candidates.FindImportedVenueResolverContextAsync(
                uuid,
                query,
                cancellationToken: ct
            );
        return await SavePreparedClaimAsync(attempt, source, context, [input], ct);
    }

    private async Task<VenueResolutionAttempt> PrepareShowAsync(
        VenueResolutionAttempt attempt,
        CancellationToken ct
    )
    {
        var source =
            await sources.PrepareShowClaimAsync(attempt.Request.Show!, ct)
            ?? throw new NormalizationConflictException(
                "The published show venue source is unavailable."
            );
        if (source.Claim?.CatalogUuid == null)
        {
            var unresolved = await candidates.FindUnboundVenueResolverContextAsync(source, ct);
            return await SavePreparedClaimAsync(attempt, source, unresolved, [], ct);
        }
        var query = Query(source.Claim.Observation);
        var context = !SupportsQuery(query)
            ? await candidates.ReadUnsupportedVenueResolverContextAsync(source, ct)
            : await candidates.FindShowVenueResolutionContextAsync(
                source,
                query,
                cancellationToken: ct
            );
        return await SavePreparedClaimAsync(attempt, source, context, [], ct);
    }

    private async Task<VenueResolutionAttempt> SavePreparedClaimAsync(
        VenueResolutionAttempt attempt,
        VenueResolutionSource source,
        VenueResolutionContext context,
        IReadOnlyList<NormalizationInputDependency> inputs,
        CancellationToken ct
    )
    {
        if (!context.SourceCurrent)
        {
            // Original and current source clocks cannot form one admissible snapshot. The
            // immutable request still identifies its historical extraction; do not save a
            // preparation that silently replaces those clocks with the current context.
            return await store.CompleteAsync(
                attempt,
                VenueResolutionDisposition.Stale,
                "source_observation_changed",
                ct
            );
        }
        var policy = await policies.ReadStateAsync(ct);
        var read = MergeVenueReadSets([
            SourceDecisionReadSet(source, context, policy.Active, inputs),
            new([], [], [], [], null, [], [], [policy.OwnerObservation], []),
        ]);
        var support =
            source.Claim == null || attempt.Request.Support == null
                ? null
                : await PrepareSupportedFactsAsync(
                    source.Claim,
                    context,
                    attempt.Request.Support,
                    ct
                );
        if (support != null)
        {
            read = MergeVenueReadSets([
                read,
                new(support.Inputs, [], [], [], null, [], [], [], []),
            ]);
        }
        var status =
            source.Claim?.CatalogUuid == null ? VenueResolutionPreparationStatus.SourceUnbound
            : SupportsQuery(Query(source.Claim.Observation))
                ? VenueResolutionPreparationStatus.Ready
            : VenueResolutionPreparationStatus.UnsupportedQuery;
        return await store.SavePreparationAsync(
            attempt.AttemptUuid,
            new(source, context, read, policy.Active, status, policy.OwnerObservation, support),
            ct
        );
    }

    private async Task<VenueResolutionAttempt> EvaluateAsync(
        VenueResolutionAttempt attempt,
        CancellationToken ct
    )
    {
        var prep = attempt.Preparation!;
        var claim = prep.Source.Claim!;
        var source = prep.Support?.Facts.Source.Match ?? SourceMatch(claim);
        var pool = prep
            .Context.Candidates.Facts.Concat(prep.Context.AssignmentTargets)
            .DistinctBy(fact => fact.Header.Uuid)
            .OrderBy(fact => fact.Header.Uuid)
            .ToArray();
        var matcher = new IdentityCandidateMatcher();
        var comparisons = pool.Select(target =>
                matcher.CompareVenues(
                    source,
                    prep.Support?.Facts.Candidates.Single(value =>
                        value.Match.Uuid == target.Header.Uuid
                    ).Match ?? CandidateMatch(target)
                )
            )
            .ToArray();
        var facts = new VenueCorrespondenceFacts(
            claim.Observation,
            claim.IsShow ? VenueResolutionSourceScope.Show : VenueResolutionSourceScope.Imported,
            source,
            prep.Context.Assignments,
            pool,
            comparisons,
            prep.Context.Complete,
            Version,
            prep.Support == null
                ? VenueResolutionPolicyEvaluator.SourceContextVersion
                : VenueResolutionPolicyEvaluator.SupportedSourceContextVersion,
            NormalizationCandidateService.FactsVersion,
            VenueResolutionPolicyEvaluator.MatcherVersion,
            prep.Support?.Facts
        );
        var decision =
            prep.Policy == null
                ? new VenueCorrespondenceDecision(
                    VenueCorrespondenceAction.NeedsReview,
                    null,
                    "",
                    "acceptance_policy_inactive",
                    []
                )
                : new VenueResolutionPolicyEvaluator().Evaluate(facts, prep.Policy.Policy);
        var target =
            decision.Action == VenueCorrespondenceAction.ProposeCreation
                ? StableUuid(attempt.AttemptUuid, "venue")
                : decision.VenueUuid;
        var current = prep.Context.Assignments.SingleOrDefault(item =>
            !item.IsHistorical && item.State == "accepted"
        );
        return await store.SaveDecisionAsync(
            attempt.AttemptUuid,
            new(
                attempt.AttemptUuid,
                Version,
                claim,
                facts,
                decision,
                prep.Policy,
                prep.ReadSet,
                target,
                current?.AssignmentUuid ?? StableUuid(attempt.AttemptUuid, "assignment"),
                current?.Revision ?? 0
            ),
            ct
        );
    }

    private async Task<VenueResolutionAttempt> AdvanceStageAsync(
        VenueResolutionAttempt attempt,
        VenueResolutionStage stage,
        CancellationToken ct
    )
    {
        if (stage.Ticket == null)
        {
            var ticket = await AdmitStageAsync(attempt, stage, ct);
            return await store.AttachTicketAsync(attempt.AttemptUuid, stage.WorkUuid, ticket, ct);
        }
        var receipt = await venues.PublishVenueResolutionAsync(
            stage.WorkUuid,
            attempt.AttemptUuid,
            ct
        );
        if (receipt.Status == NormalizationPublicationStatus.Stale)
        {
            return await store.CompleteAsync(
                attempt,
                VenueResolutionDisposition.Stale,
                "publication_stale",
                ct
            );
        }
        return (await store.GetAsync(attempt.AttemptUuid, ct))!;
    }

    private static Guid StableUuid(Guid attempt, string phase) =>
        new(
            SHA256
                .HashData(
                    Encoding.UTF8.GetBytes(Version + "\n" + attempt.ToString("D") + "\n" + phase)
                )
                .AsSpan(0, 16)
        );

    private static NormalizationCandidateQuery Query(ProviderVenueObservation observation)
    {
        // The fixture marker is included both literally and without its explicit kind annotation
        // so a previously created premise remains in the candidate pool on later attempts.
        var names = observation
            .Names.Select(value => value.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .SelectMany(value =>
                value.StartsWith("[site] ", StringComparison.Ordinal)
                    ? new[] { value, value[7..].Trim() }
                    : new[] { value }
            )
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return new(
            IdentityMatchingDomain.Venues,
            names,
            observation
                .Identities.Select(value => new MatchingExternalIdentity(
                    value.Identity.Provider,
                    value.Identity.ResourceType,
                    value.Identity.ProviderKey
                ))
                .ToArray(),
            NameLimit: 200
        );
    }

    private static VenueMatchRecord SourceMatch(VenueResolutionClaim claim) =>
        new(
            null,
            claim.Observation.Names.Count == 1
            && claim.Observation.Names[0].Value.StartsWith("[site] ", StringComparison.Ordinal)
                ? "site"
                : "",
            null,
            claim.Observation.Names.Select(name => new MatchingVenueName(name.Value)).ToArray(),
            claim
                .Observation.Identities.Select(identity => new MatchingExternalIdentity(
                    identity.Identity.Provider,
                    identity.Identity.ResourceType,
                    identity.Identity.ProviderKey
                ))
                .ToArray(),
            new(null, null, claim.Observation.Address?.Value),
            claim.ObservedDate?.Value,
            []
        );

    private static bool SupportsQuery(NormalizationCandidateQuery query) =>
        query.Names.Count + query.ExternalIdentities.Count > 0
        && query.Names.Count <= 16
        && query.ExternalIdentities.Count <= 16
        && query
            .Names.Select(name => NormalizedPeopleService.NameKey(name.Trim()))
            .All(name =>
                name.EnumerateRunes().Count() is >= 1 and <= 200 && name.Any(char.IsLetterOrDigit)
            )
        && query.ExternalIdentities.All(identity =>
            !string.IsNullOrWhiteSpace(identity.Provider)
            && !string.IsNullOrWhiteSpace(identity.ResourceType)
            && !string.IsNullOrWhiteSpace(identity.ProviderKey)
        );

    private static Guid? SingleCandidateParent(VenueCandidateFacts candidate)
    {
        var parents = candidate
            .Parents.Select(parent => parent.ResolvedParentVenueUuid)
            .Distinct()
            .ToArray();
        return parents.Length == 1 ? parents[0] : null;
    }

    private static VenueMatchRecord CandidateMatch(VenueCandidateFacts candidate) =>
        new(
            candidate.Header.Uuid,
            candidate.Header.Kind ?? "",
            SingleCandidateParent(candidate),
            candidate
                .Names.Select(name => new MatchingVenueName(
                    name.Wording,
                    name.ValidFrom,
                    name.ValidTo,
                    name.Language
                ))
                .ToArray(),
            candidate
                .ExternalIdentities.Select(identity => new MatchingExternalIdentity(
                    identity.Provider,
                    identity.ResourceType,
                    identity.ProviderKey
                ))
                .ToArray(),
            null,
            LocationAssertions: candidate
                .Locations.Select(location => new MatchingVenueLocationAssertion(
                    new(null, null, location.Address),
                    location.ValidFrom,
                    location.ValidTo
                ))
                .ToArray()
        );
}
