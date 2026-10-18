using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationCompositionResolutionProcessor
{
    private const int MaximumSearchVariants = 3;
    private const int SearchPageSize = 50;
    private const int MaximumSearchPages = 4;
    private const int MaximumHydrationLookups = 20;

    // Freshness is an acquisition contract, not a correspondence acceptance threshold.
    private static readonly TimeSpan AcquisitionCacheAge = TimeSpan.FromDays(7);
    private static readonly NormalizationInputReadLimits AcquisitionReadLimits = new(
        32,
        8 * 1024 * 1024,
        128 * 1024 * 1024
    );

    private static CompositionResolutionSearchCheckpoint? SearchCheckpoint(
        CompositionResolutionAttempt attempt
    ) => attempt.Checkpoints.LastOrDefault(checkpoint => checkpoint.Search != null)?.Search;

    private static CompositionResolutionHydrationCheckpoint? HydrationCheckpoint(
        CompositionResolutionAttempt attempt
    ) => attempt.Checkpoints.LastOrDefault(checkpoint => checkpoint.Hydration != null)?.Hydration;

    private static bool AcquisitionComplete(CompositionResolutionAttempt attempt) =>
        SearchCheckpoint(attempt)?.Complete == true
        && HydrationCheckpoint(attempt)?.Complete == true;

    /// <summary>
    /// Freeze requests before sending. A call may checkpoint several local phase transitions,
    /// but invokes only one acquisition command; Pending never holds this processor asleep.
    /// </summary>
    private async Task<CompositionResolutionAttempt> AdvanceAcquisitionAsync(
        CompositionResolutionAttempt attempt,
        CancellationToken ct
    )
    {
        var preparation =
            attempt.Preparation
            ?? throw new InvalidOperationException("Acquisition requires a saved preparation.");
        var search = SearchCheckpoint(attempt);
        if (search == null)
        {
            var issues = preparation.Variants.Issues.ToList();
            if (preparation.Variants.Queries.Count > MaximumSearchVariants)
            {
                issues.Add("SearchVariantLimit");
            }

            search = new(
                preparation
                    .Variants.Queries.Take(MaximumSearchVariants)
                    .Select((query, ordinal) => NewSearchPage(attempt, query, ordinal, 0))
                    .ToArray(),
                issues,
                false
            );
            var saved = await store.SaveSearchAsync(attempt, search, ct);
            if (!SavedAcquisitionCheckpoint(attempt, saved, search))
            {
                return saved;
            }

            attempt = saved;
        }

        if (!search.Complete)
        {
            var pending = search.Pages.FirstOrDefault(page => !Terminal(page.Acquisition?.State));
            if (pending != null)
            {
                return await AdvanceSearchPageAsync(attempt, search, pending, ct);
            }

            var retained = await ReadSearchPagesAsync(search, ct);
            var next = PlanNextSearchPages(attempt, search, retained);
            var saved = await store.SaveSearchAsync(attempt, next, ct);
            if (!SavedAcquisitionCheckpoint(attempt, saved, next))
            {
                return saved;
            }

            attempt = saved;
            search = next;
            pending = search.Pages.FirstOrDefault(page => !Terminal(page.Acquisition?.State));
            if (pending != null)
            {
                return await AdvanceSearchPageAsync(attempt, search, pending, ct);
            }
        }

        return await AdvanceHydrationAsync(attempt, search, ct);
    }

    private async Task<CompositionResolutionAttempt> AdvanceSearchPageAsync(
        CompositionResolutionAttempt attempt,
        CompositionResolutionSearchCheckpoint search,
        CompositionSearchPage page,
        CancellationToken ct
    )
    {
        if (page.Acquisition?.NotBefore > DateTimeOffset.UtcNow)
        {
            return attempt;
        }

        // Reusing the durable child command also recovers a response saved before checkpoint CAS.
        var acquisition = await inputs.AcquireSearchAsync(page.CommandUuid, page.Request, ct);
        if (JsonSerializer.Serialize(acquisition) == JsonSerializer.Serialize(page.Acquisition))
        {
            return attempt;
        }

        var updated = search with
        {
            Pages = search
                .Pages.Select(value =>
                    value.CommandUuid == page.CommandUuid
                        ? value with
                        {
                            Acquisition = acquisition,
                        }
                        : value
                )
                .ToArray(),
        };
        return await store.SaveSearchAsync(attempt, updated, ct);
    }

    private CompositionSearchPage NewSearchPage(
        CompositionResolutionAttempt attempt,
        MusicBrainzSearchQuery query,
        int variant,
        int offset
    )
    {
        var preparationHash = Convert
            .ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(JsonSerializer.Serialize(attempt.Preparation))
                )
            )
            .ToLowerInvariant();
        return new(
            AcquisitionChildUuid(attempt.AttemptUuid, $"search/{variant}/{offset}"),
            new(
                query,
                offset,
                MusicBrainzCacheMode.ReuseFresh,
                AcquisitionCacheAge,
                new(attempt.AttemptUuid, preparationHash)
            ),
            null
        );
    }

    private CompositionResolutionSearchCheckpoint PlanNextSearchPages(
        CompositionResolutionAttempt attempt,
        CompositionResolutionSearchCheckpoint search,
        IReadOnlyList<RetainedCompositionSearchPage> retained
    )
    {
        var pages = search.Pages.ToList();
        var issues = search.Issues.Concat(retained.SelectMany(page => page.Issues)).ToList();
        var seen = new HashSet<NormalizationResourceKey>();
        foreach (var page in retained)
        {
            foreach (var hit in page.Observation?.Hits ?? [])
            {
                if (!seen.Add(hit.Entity.Identity.Identity))
                {
                    issues.Add($"DuplicateSearchHit:{page.Page.CommandUuid:D}:{hit.SourceIndex}");
                }
            }
        }

        var queries = attempt.Preparation!.Variants.Queries.Take(MaximumSearchVariants).ToArray();
        for (var variant = 0; variant < queries.Length; variant++)
        {
            var queryPages = retained
                .Where(page =>
                    page.Page.CommandUuid
                    == AcquisitionChildUuid(
                        attempt.AttemptUuid,
                        $"search/{variant}/{page.Page.Request.Offset}"
                    )
                )
                .OrderBy(page => page.Page.Request.Offset)
                .ToArray();
            var last = queryPages.Last();
            var counts = queryPages
                .Select(page => page.Observation?.Count?.Value)
                .Where(value => value.HasValue)
                .Distinct()
                .ToArray();
            if (counts.Length > 1)
            {
                issues.Add($"SearchCountDrift:{variant}");
            }

            for (var index = 0; index < queryPages.Length; index++)
            {
                if (queryPages[index].Page.Request.Offset != index * SearchPageSize)
                {
                    issues.Add($"SearchPageGap:{variant}:{index}");
                }
            }

            if (last.Observation?.Count == null || last.RawMemberCount == null)
            {
                continue;
            }

            var nextOffset = last.Page.Request.Offset + SearchPageSize;
            var remaining =
                last.Observation.Count.Value > last.Page.Request.Offset + last.RawMemberCount.Value;
            if (last.RawMemberCount.Value < SearchPageSize)
            {
                if (remaining)
                {
                    issues.Add(
                        $"ShortSearchPageBeforeReportedEnd:{variant}:{last.Page.Request.Offset}"
                    );
                }

                continue;
            }

            if (last.Observation.Count.Value <= nextOffset)
            {
                continue;
            }

            if (queryPages.Length == MaximumSearchPages)
            {
                issues.Add($"SearchPageLimit:{variant}");
                continue;
            }

            pages.Add(NewSearchPage(attempt, queries[variant], variant, nextOffset));
        }

        var complete = pages.Count == search.Pages.Count;
        var observedEnd =
            complete
            && queries.Length > 0
            && Enumerable
                .Range(0, queries.Length)
                .All(variant =>
                {
                    var queryPages = retained
                        .Where(page =>
                            page.Page.CommandUuid
                            == AcquisitionChildUuid(
                                attempt.AttemptUuid,
                                $"search/{variant}/{page.Page.Request.Offset}"
                            )
                        )
                        .OrderBy(page => page.Page.Request.Offset)
                        .ToArray();
                    var last = queryPages.Last();
                    return last.Observation?.Count is { } count
                        && last.RawMemberCount is { } members
                        && last.Page.Request.Offset + members >= count.Value
                        && queryPages
                            .Select(page => page.Observation?.Count?.Value)
                            .Distinct()
                            .Count() == 1;
                });
        // Exhausting a plan (including failures or bounds) is separate from witnessing the
        // reported end of each query window. Neither proves an external universe complete.
        return new(pages, issues.Distinct(StringComparer.Ordinal).ToArray(), complete, observedEnd);
    }

    private async Task<CompositionResolutionAttempt> AdvanceHydrationAsync(
        CompositionResolutionAttempt attempt,
        CompositionResolutionSearchCheckpoint search,
        CancellationToken ct
    )
    {
        var hydration = HydrationCheckpoint(attempt);
        if (hydration?.Complete == true)
        {
            return attempt;
        }

        if (
            hydration == null
            || hydration.Lookups.All(lookup => Terminal(lookup.Acquisition?.State))
        )
        {
            var next = await PlanHydrationAsync(attempt, search, hydration, ct);
            var saved = await store.SaveHydrationAsync(attempt, next, ct);
            if (!SavedAcquisitionCheckpoint(attempt, saved, next))
            {
                return saved;
            }

            attempt = saved;
            hydration = next;
        }

        var pending = hydration.Lookups.FirstOrDefault(lookup =>
            !Terminal(lookup.Acquisition?.State)
        );
        if (pending == null || pending.Acquisition?.NotBefore > DateTimeOffset.UtcNow)
        {
            return attempt;
        }

        var acquisition = await inputs.AcquireAsync(pending.CommandUuid, pending.Request, ct);
        if (JsonSerializer.Serialize(acquisition) == JsonSerializer.Serialize(pending.Acquisition))
        {
            return attempt;
        }

        return await store.SaveHydrationAsync(
            attempt,
            hydration with
            {
                Lookups = hydration
                    .Lookups.Select(value =>
                        value.CommandUuid == pending.CommandUuid
                            ? value with
                            {
                                Acquisition = acquisition,
                            }
                            : value
                    )
                    .ToArray(),
            },
            ct
        );
    }

    private async Task<CompositionResolutionHydrationCheckpoint> PlanHydrationAsync(
        CompositionResolutionAttempt attempt,
        CompositionResolutionSearchCheckpoint search,
        CompositionResolutionHydrationCheckpoint? previous,
        CancellationToken ct
    )
    {
        var lookups = previous?.Lookups.ToList() ?? [];
        var issues = previous?.Issues.ToList() ?? [];
        var requested = lookups
            .Select(lookup => (lookup.Request.Kind, lookup.Request.Mbid))
            .ToHashSet();
        var targets = new List<ObservedProviderIdentity>();
        foreach (var page in await ReadSearchPagesAsync(search, ct))
        {
            targets.AddRange((page.Observation?.Hits ?? []).Select(hit => hit.Entity.Identity));
        }

        var decoded = await ReadHydratedInputsAsync(lookups, ct);
        // Each completed lookup can reveal relationship targets for the next invocation.
        // Discovery validates observations; this method owns queue order, dedupe and bounds.
        foreach (var lookup in lookups)
        {
            decoded.TryGetValue(lookup.CommandUuid, out var facts);
            var discovered = DiscoverHydrationTargets(lookup, facts);
            targets.AddRange(discovered.Targets);
            issues.AddRange(discovered.Issues);
        }

        // Retained discovery order determines transport scheduling only. Every omitted resource
        // remains explicit; the first twenty have no rank-derived acceptance privilege.
        foreach (var target in targets)
        {
            if (!TryLookupIdentity(target, out var kind, out var mbid))
            {
                issues.Add(
                    $"InvalidHydrationTarget:{target.Identity.Provider}:{target.Identity.ResourceType}:{target.Identity.ProviderKey}"
                );
                continue;
            }

            if (!requested.Add((kind, mbid)))
            {
                continue;
            }

            if (lookups.Count == MaximumHydrationLookups)
            {
                issues.Add($"HydrationLimit:{kind}:{mbid:D}");
                continue;
            }

            lookups.Add(
                new(
                    AcquisitionChildUuid(attempt.AttemptUuid, $"lookup/{kind}/{mbid:D}"),
                    new(kind, mbid, MusicBrainzCacheMode.ReuseFresh, AcquisitionCacheAge),
                    null
                )
            );
        }

        return new(
            lookups,
            issues.Distinct(StringComparer.Ordinal).ToArray(),
            lookups.All(lookup => Terminal(lookup.Acquisition?.State))
        );
    }

    private static CompositionHydrationDiscovery DiscoverHydrationTargets(
        CompositionHydrationLookup lookup,
        ProviderDecodeResult? facts
    )
    {
        if (facts == null)
        {
            return new(
                [],
                [
                    $"HydrationUnavailable:{lookup.Request.Kind}:{lookup.Request.Mbid:D}:{lookup.Acquisition?.Result?.Reason}",
                ]
            );
        }
        var targets = new List<ObservedProviderIdentity>();
        var issues = new List<string>();
        issues.AddRange(
            facts.Issues.Select(issue => $"HydrationDecode:{lookup.CommandUuid:D}:{issue.Code}")
        );
        var entityCount = lookup.Request.Kind switch
        {
            MusicBrainzLookupKind.Work => facts.MusicBrainzWorks?.Count ?? 0,
            MusicBrainzLookupKind.Recording => facts.MusicBrainzRecordings?.Count ?? 0,
            MusicBrainzLookupKind.Artist => facts.MusicBrainzArtists?.Count ?? 0,
            _ => 0,
        };
        if (entityCount != 1)
        {
            issues.Add($"HydrationEntityCount:{lookup.CommandUuid:D}:{entityCount}");
        }

        foreach (var recording in facts.MusicBrainzRecordings ?? [])
        {
            if (recording.Relationships == null)
            {
                issues.Add($"MissingRecordingRelationships:{lookup.Request.Mbid:D}");
            }

            targets.AddRange(
                (recording.Relationships ?? [])
                    .Where(relation => relation.Target?.Identity.Identity.ResourceType == "work")
                    .Select(relation => relation.Target!.Identity)
            );
        }

        foreach (var work in facts.MusicBrainzWorks ?? [])
        {
            if (work.Relationships == null)
            {
                issues.Add($"MissingWorkRelationships:{lookup.Request.Mbid:D}");
            }

            targets.AddRange(
                (work.Relationships ?? [])
                    .Where(relation => relation.Target?.Identity.Identity.ResourceType == "artist")
                    .Select(relation => relation.Target!.Identity)
            );
        }
        return new(targets, issues);
    }

    private sealed record CompositionHydrationDiscovery(
        IReadOnlyList<ObservedProviderIdentity> Targets,
        IReadOnlyList<string> Issues
    );

    private async Task<CompositionCorrespondenceFacts> ReadCorrespondenceFactsAsync(
        CompositionResolutionAttempt attempt,
        CancellationToken ct
    )
    {
        if (!AcquisitionComplete(attempt))
        {
            throw new InvalidOperationException(
                "Correspondence facts require terminal acquisition checkpoints."
            );
        }

        var context = attempt.Preparation!.Context;
        var hydration = HydrationCheckpoint(attempt)!;
        var decoded = await ReadHydratedInputsAsync(hydration.Lookups, ct);
        var recordings = decoded
            .Values.SelectMany(value =>
                (value.MusicBrainzRecordings ?? []).Select(
                    recording => new RetainedCompositionRecording(recording, value.Issues)
                )
            )
            .ToArray();
        var candidates = new List<CompositionCorrespondenceCandidate>();
        foreach (
            var lookup in hydration.Lookups.Where(value =>
                value.Request.Kind == MusicBrainzLookupKind.Work
            )
        )
        {
            if (!decoded.TryGetValue(lookup.CommandUuid, out var facts))
            {
                continue;
            }

            foreach (var work in facts.MusicBrainzWorks ?? [])
            {
                var contradictions = facts
                    .Issues.Select(issue => $"work_decode:{issue.Code}")
                    .ToList();
                if (
                    !TryLookupIdentity(work.Work.Identity, out var kind, out var mbid)
                    || kind != MusicBrainzLookupKind.Work
                    || mbid != lookup.Request.Mbid
                )
                {
                    contradictions.Add("hydrated_work_identity_mismatch");
                }

                var relatedRecordings = recordings
                    .Where(value => RecordingTargetsWork(value.Observation, lookup.Request.Mbid))
                    .ToArray();

                // A decoded good relationship cannot conceal a malformed rival member in the
                // same retained recording. Unknown role strings are preserved observations;
                // decoder diagnostics instead identify fields that could not be read reliably.
                contradictions.AddRange(
                    relatedRecordings.SelectMany(value =>
                        value.Issues.Select(issue =>
                            $"recording_decode:{value.Observation.Recording.Identity.Identity.ProviderKey}:{issue.Code}:{issue.Evidence.JsonPointer}"
                        )
                    )
                );

                candidates.Add(
                    new(
                        lookup.Request.Mbid,
                        work,
                        relatedRecordings.Select(value => value.Observation).ToArray(),
                        contradictions
                    )
                );
            }
        }

        return new(
            context.Claim.RepertoireSongUuid,
            context.LocalTitle
                ?? throw new InvalidOperationException(
                    "Correspondence evaluation requires an evidenced local title."
                ),
            context.Artist,
            candidates,
            context.Candidates.Complete,
            false,
            context
                .Issues.Concat(SearchCheckpoint(attempt)!.Issues)
                .Concat(hydration.Issues)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            CompositionResolutionPolicyEvaluator.ProcessorVersion,
            CompositionResolutionPolicyEvaluator.FactsVersion,
            MusicBrainzCandidateQueryBuilder.Version,
            NormalizationProviderDecoder.Version,
            RetainedRecordingInventory: attempt.Preparation.Policy?.Policy.Artifact.FixtureOnly
            == true
                ? null
                : CompositionRetainedRecordingInventoryBuilder.Build(hydration.Lookups, decoded)
        );
    }

    private static bool RecordingTargetsWork(
        MusicBrainzRecordingObservation recording,
        Guid workMbid
    ) =>
        (recording.Relationships ?? []).Any(relation =>
            relation.Target is { } target
            && TryLookupIdentity(target.Identity, out var kind, out var mbid)
            && kind == MusicBrainzLookupKind.Work
            && mbid == workMbid
        );

    private async Task<IReadOnlyList<RetainedCompositionSearchPage>> ReadSearchPagesAsync(
        CompositionResolutionSearchCheckpoint search,
        CancellationToken ct
    )
    {
        var references = search
            .Pages.Where(page => page.Acquisition?.State == MusicBrainzAcquisitionState.Completed)
            .Select(page => page.Acquisition!.Result?.Input)
            .OfType<NormalizationBindingInputReference>()
            .Distinct()
            .ToArray();
        IReadOnlyList<NormalizationInputRevision> retained =
            references.Length == 0
                ? []
                : await evidence.GetInputsAsync(references, AcquisitionReadLimits, ct);
        var byUuid = retained.ToDictionary(input => input.Uuid);
        var pages = new List<RetainedCompositionSearchPage>();
        foreach (var page in search.Pages)
        {
            var issues = new List<string>();
            MusicBrainzSearchPageObservation? observation = null;
            int? rawMemberCount = null;
            if (
                page.Acquisition?.State == MusicBrainzAcquisitionState.Completed
                && page.Acquisition.Result?.Input is { } reference
                && byUuid.TryGetValue(reference.InputRevisionUuid, out var input)
            )
            {
                var facts = decoder.Decode(input, ct);
                observation = facts.MusicBrainzSearchPages?.SingleOrDefault();
                issues.AddRange(
                    facts.Issues.Select(issue => $"SearchDecode:{page.CommandUuid:D}:{issue.Code}")
                );
                issues.AddRange(
                    (observation?.Issues ?? []).Select(issue =>
                        $"SearchPageDecode:{page.CommandUuid:D}:{issue.Code}"
                    )
                );
                using var document = JsonDocument.Parse(input.Payload);
                var memberName = page.Request.Query.Kind switch
                {
                    MusicBrainzLookupKind.Work => "works",
                    MusicBrainzLookupKind.Recording => "recordings",
                    MusicBrainzLookupKind.Artist => "artists",
                    _ => throw new InvalidOperationException("Unsupported search kind."),
                };
                if (
                    document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty(memberName, out var members)
                    && members.ValueKind == JsonValueKind.Array
                )
                {
                    rawMemberCount = members.GetArrayLength();
                }

                if (observation?.Count?.Value == 0 && rawMemberCount == 0)
                {
                    issues.Add($"ObservedZeroSearchPage:{page.CommandUuid:D}");
                }

                if (
                    observation?.Hits == null
                    || observation.Count == null
                    || observation.Offset?.Value != page.Request.Offset
                    || rawMemberCount == null
                )
                {
                    issues.Add($"MalformedSearchPage:{page.CommandUuid:D}");
                }
            }
            else
            {
                issues.Add(
                    $"SearchPageUnavailable:{page.CommandUuid:D}:{page.Acquisition?.Result?.Reason}"
                );
            }

            pages.Add(new(page, observation, rawMemberCount, issues));
        }

        return pages;
    }

    private async Task<Dictionary<Guid, ProviderDecodeResult>> ReadHydratedInputsAsync(
        IReadOnlyList<CompositionHydrationLookup> lookups,
        CancellationToken ct
    )
    {
        var references = lookups
            .Where(lookup => lookup.Acquisition?.State == MusicBrainzAcquisitionState.Completed)
            .Select(lookup => lookup.Acquisition!.Result?.Input)
            .OfType<NormalizationBindingInputReference>()
            .Distinct()
            .ToArray();
        IReadOnlyList<NormalizationInputRevision> retained =
            references.Length == 0
                ? []
                : await evidence.GetInputsAsync(references, AcquisitionReadLimits, ct);
        var decoded = retained.ToDictionary(
            input => input.Uuid,
            input => decoder.Decode(input, ct)
        );
        return lookups
            .Where(lookup =>
                lookup.Acquisition?.State == MusicBrainzAcquisitionState.Completed
                && lookup.Acquisition.Result?.Input is { } reference
                && decoded.ContainsKey(reference.InputRevisionUuid)
            )
            .ToDictionary(
                lookup => lookup.CommandUuid,
                lookup => decoded[lookup.Acquisition!.Result!.Input!.InputRevisionUuid]
            );
    }

    private static bool TryLookupIdentity(
        ObservedProviderIdentity identity,
        out MusicBrainzLookupKind kind,
        out Guid mbid
    )
    {
        mbid = Guid.Empty;
        kind = identity.Identity.ResourceType switch
        {
            "work" => MusicBrainzLookupKind.Work,
            "recording" => MusicBrainzLookupKind.Recording,
            "artist" => MusicBrainzLookupKind.Artist,
            _ => (MusicBrainzLookupKind)(-1),
        };
        return identity.Identity.Provider == "musicbrainz.org"
            && (int)kind >= 0
            && Guid.TryParse(identity.Identity.ProviderKey, out mbid)
            && mbid != Guid.Empty;
    }

    private static bool Terminal(MusicBrainzAcquisitionState? state) =>
        state is MusicBrainzAcquisitionState.Completed or MusicBrainzAcquisitionState.Failed;

    private static Guid AcquisitionChildUuid(Guid attemptUuid, string child) =>
        new(
            SHA256.HashData(
                Encoding.UTF8.GetBytes(
                    $"{CompositionResolutionPolicyEvaluator.ProcessorVersion}\n{attemptUuid:D}\n{child}"
                )
            )[..16]
        );

    private static bool SavedAcquisitionCheckpoint<T>(
        CompositionResolutionAttempt before,
        CompositionResolutionAttempt after,
        T payload
    )
    {
        if (after.Checkpoints.Count != before.Checkpoints.Count + 1)
        {
            return false;
        }

        object? saved =
            payload is CompositionResolutionSearchCheckpoint
                ? after.Checkpoints[^1].Search
                : after.Checkpoints[^1].Hydration;
        return JsonSerializer.Serialize(saved) == JsonSerializer.Serialize(payload);
    }

    private sealed record RetainedCompositionSearchPage(
        CompositionSearchPage Page,
        MusicBrainzSearchPageObservation? Observation,
        int? RawMemberCount,
        IReadOnlyList<string> Issues
    );

    private sealed record RetainedCompositionRecording(
        MusicBrainzRecordingObservation Observation,
        IReadOnlyList<ProviderDecodeIssue> Issues
    );
}
