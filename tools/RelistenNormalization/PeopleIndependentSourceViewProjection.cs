using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization.Batch;

/// <summary>Retains independently supported source views without merging fields or asserting actor identity.</summary>
internal sealed class PeopleIndependentSourceViewProjection
{
    public const string Version = "people-independent-source-views-v1";
    private readonly PeopleAdjudicatedSourcePackage package;
    private readonly PeopleAdjudicatedEndpointProjection projector;
    private readonly CancellationToken cancellationToken;
    private readonly Dictionary<(string EvidenceId, string Field), string> fieldHashes = [];

    public PeopleIndependentSourceViewProjection(
        PeopleAdjudicatedSourcePackage package,
        CancellationToken ct
    )
    {
        this.package = package;
        cancellationToken = ct;
        // The existing projector parses each complete original field once. Every selector
        // below consults that same extraction; no role parser or cross-field union is added.
        projector = new(package, ct);
    }

    public PeopleEndpointViewDiagnostic Project(PeopleAdjudicatedSourcePackage.Endpoint endpoint)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var original = projector.Project(endpoint).Diagnostic;
        var views = ReadIndependentViews(endpoint, original);
        var coveredSelectors = views.SelectMany(view => view.SelectorIndexes).ToHashSet();
        var issues = original
            .Selectors.SelectMany(selector => selector.Issues)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return new(
            endpoint.Id,
            endpoint.EvidenceId,
            endpoint.ArtistUuid,
            endpoint.MentionKind,
            Coverage(original.Selectors, coveredSelectors, views.Count),
            MatchRecordsAgree(views),
            issues,
            original.Selectors,
            original.Variants,
            views
        );
    }

    private IReadOnlyList<PeopleIndependentSourceView> ReadIndependentViews(
        PeopleAdjudicatedSourcePackage.Endpoint endpoint,
        PeopleAdjudicatedEndpointDiagnostic original
    )
    {
        var views = new Dictionary<PeopleSourceViewKey, PeopleIndependentSourceView>();
        var variantsBySelector = original.Variants.ToLookup(variant => variant.SelectorIndex);
        foreach (var selector in original.Selectors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var variants = variantsBySelector[selector.SelectorIndex].ToArray();
            if (!Eligible(selector, variants))
            {
                continue;
            }
            var variant = variants[0];
            var key = ViewKey(endpoint, selector, variant);
            if (views.TryGetValue(key, out var existing))
            {
                // Repeated alias or joint-role selectors may select the same scoped actor.
                // Keep one existing record; source assertions from other keys never enrich it.
                views[key] = existing with
                {
                    SelectorIndexes = existing
                        .SelectorIndexes.Append(selector.SelectorIndex)
                        .ToArray(),
                };
            }
            else
            {
                views.Add(
                    key,
                    new(key, [selector.SelectorIndex], variant.Projection, variant.Facts)
                );
            }
        }
        return views.Values.ToArray();
    }

    private static bool Eligible(
        PeopleAdjudicatedSelectorDiagnostic selector,
        IReadOnlyList<PeopleAdjudicatedActorVariant> variants
    )
    {
        if (variants.Count != 1 || !variants[0].Projection.Complete || Ambiguous(selector))
        {
            return false;
        }
        return selector.Status == "matched" && variants[0].ActorReference != null
            || selector.Status == "name_only"
                && selector.Kind == "actor_name"
                && variants[0].ActorReference == null;
    }

    private PeopleSourceViewKey ViewKey(
        PeopleAdjudicatedSourcePackage.Endpoint endpoint,
        PeopleAdjudicatedSelectorDiagnostic selector,
        PeopleAdjudicatedActorVariant variant
    )
    {
        if (variant.ActorReference is { } actor)
        {
            return new(
                "source_actor",
                actor.EvidenceId,
                actor.Field,
                actor.ReferenceUuid,
                null,
                null
            );
        }
        var field = selector.Evidence.Field;
        var coordinate = (endpoint.EvidenceId, field);
        if (!fieldHashes.TryGetValue(coordinate, out var hash))
        {
            var raw = package.Sources[endpoint.EvidenceId].Fields[field]!;
            hash = Convert
                .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))
                .ToLowerInvariant();
            fieldHashes.Add(coordinate, hash);
        }
        return new("source_selector", endpoint.EvidenceId, field, null, selector.Evidence, hash);
    }

    private static string Coverage(
        IReadOnlyList<PeopleAdjudicatedSelectorDiagnostic> selectors,
        IReadOnlySet<int> coveredSelectors,
        int viewCount
    )
    {
        // Completeness describes supported declared selectors and query bounds. It does not
        // establish exhaustive actor facts, agreement between views, or co-reference.
        if (selectors.All(selector => coveredSelectors.Contains(selector.SelectorIndex)))
        {
            return "complete";
        }
        if (selectors.Any(Ambiguous))
        {
            return "ambiguous";
        }
        if (selectors.Any(selector => selector.Status == "unattributed"))
        {
            return "unattributed";
        }
        return viewCount > 0 ? "partial" : "unsupported";
    }

    private static bool Ambiguous(PeopleAdjudicatedSelectorDiagnostic selector) =>
        selector.Status == "ambiguous"
        || selector.Issues.Contains("ambiguous_source_actor", StringComparer.Ordinal);

    private static bool? MatchRecordsAgree(IReadOnlyList<PeopleIndependentSourceView> views)
    {
        if (views.Count == 0)
        {
            return null;
        }
        var first = JsonSerializer.SerializeToElement(views[0].Projection.Record);
        return views
            .Skip(1)
            .All(view =>
                JsonElement.DeepEquals(
                    first,
                    JsonSerializer.SerializeToElement(view.Projection.Record)
                )
            );
    }
}

public sealed record PeopleSourceViewKey(
    string Kind,
    string EvidenceId,
    string Field,
    Guid? ReferenceUuid,
    NormalizationTextEvidence? Anchor,
    string? FieldSha256
);

public sealed record PeopleIndependentSourceView(
    PeopleSourceViewKey Key,
    IReadOnlyList<int> SelectorIndexes,
    PeopleActorProjectionResult Projection,
    CreditedActorFacts<NormalizationTextEvidence>? Facts
);

public sealed record PeopleEndpointViewDiagnostic(
    string EndpointId,
    string EvidenceId,
    Guid ArtistUuid,
    string? MentionKind,
    string Coverage,
    bool? MatchRecordsAgree,
    IReadOnlyList<string> Issues,
    IReadOnlyList<PeopleAdjudicatedSelectorDiagnostic> Selectors,
    IReadOnlyList<PeopleAdjudicatedActorVariant> Variants,
    IReadOnlyList<PeopleIndependentSourceView> Views
);
