using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization.Batch;

/// <summary>Projects every source selector before deciding whether an endpoint has one usable actor.</summary>
internal sealed class PeopleAdjudicatedEndpointProjection
{
    public const string Version = "people-adjudicated-source-endpoints-v1";
    private readonly PeopleSourceContextProjection projector;

    public PeopleAdjudicatedEndpointProjection(
        PeopleAdjudicatedSourcePackage package,
        CancellationToken ct
    )
    {
        projector = new(
            package.Sources.ToDictionary(pair => pair.Key, pair => pair.Value.Fields),
            ct
        );
    }

    public Result Project(PeopleAdjudicatedSourcePackage.Endpoint endpoint)
    {
        var selectors = new List<PeopleAdjudicatedSelectorDiagnostic>();
        var variants = new List<PeopleAdjudicatedActorVariant>();
        for (var index = 0; index < endpoint.Selectors.Count; index++)
        {
            ProjectSelector(endpoint, index, selectors, variants);
        }
        if (variants.Count > 1000)
        {
            throw new InvalidDataException("A source endpoint exceeds 1,000 actor variants.");
        }
        var references = variants
            .Where(variant => variant.ActorReference != null)
            .Select(variant => variant.ActorReference!)
            .Distinct()
            .ToArray();
        var allMatched = selectors.All(selector => selector.Status == "matched");
        PeopleActorProjectionResult? projection = null;
        var status = "unsupported";
        string? issue;
        if (allMatched && references.Length == 1)
        {
            // Every selector independently selects the same field-local actor. The repeated
            // projections come from that one extractor result, not an equality of names/keys.
            projection = variants[0].Projection;
            status = "projected";
            issue = projection.Complete ? null : "context_query_incomplete";
        }
        else if (
            selectors.Count == 1
            && selectors[0].Kind == "actor_name"
            && selectors[0].Status == "name_only"
        )
        {
            projection = variants[0].Projection;
            status = "name_only";
            issue = projection.Complete ? null : "context_query_incomplete";
        }
        else if (references.Length > 1 || selectors.Any(selector => selector.Status == "ambiguous"))
        {
            status = "ambiguous";
            issue = "multiple_source_actors";
        }
        else if (selectors.Any(selector => selector.Status == "unattributed"))
        {
            status = "unattributed";
            issue = "unattributed_source_reference";
        }
        else if (selectors.Any(selector => selector.Status == "unsupported_mention_expression"))
        {
            issue = "unsupported_mention_expression";
        }
        else
        {
            status = "not_found";
            issue = "source_actor_not_found";
        }
        if (issue != null)
        {
            projection = null;
        }
        return new(
            new(
                endpoint.Id,
                endpoint.EvidenceId,
                endpoint.ArtistUuid,
                status,
                issue,
                selectors,
                variants,
                endpoint.MentionKind
            ),
            projection
        );
    }

    private void ProjectSelector(
        PeopleAdjudicatedSourcePackage.Endpoint endpoint,
        int index,
        List<PeopleAdjudicatedSelectorDiagnostic> selectors,
        List<PeopleAdjudicatedActorVariant> variants
    )
    {
        var selector = endpoint.Selectors[index];
        if (selector.Kind is "actor_name" or "source_expression")
        {
            // New exact name selectors may omit the original annotation's mention kind.
            // Route by the authenticated selector, while keeping its nullable metadata intact.
            var result = projector.Project(
                endpoint.EvidenceId,
                selector.Evidence.Quote,
                selector.Kind == "actor_name" ? "name" : endpoint.MentionKind!,
                endpoint.ArtistUuid,
                selector.Evidence
            );
            selectors.Add(
                new(index, selector.Kind, selector.Evidence, result.Status, result.Issues)
            );
            variants.Add(
                new(
                    index,
                    result.ReferenceUuid is { } reference
                        ? new(endpoint.EvidenceId, selector.Evidence.Field, reference)
                        : null,
                    selector.Evidence.Quote,
                    null,
                    result.Projection,
                    result.Facts
                )
            );
            return;
        }
        var kind =
            selector.Kind == "source_contact_reference"
                ? PeopleSourceReferenceKind.Contact
                : PeopleSourceReferenceKind.Profile;
        var referenceResult = projector.ProjectReference(
            endpoint.EvidenceId,
            endpoint.ArtistUuid,
            new(kind, selector.Evidence)
        );
        var status = referenceResult.Status switch
        {
            PeopleSourceReferenceStatus.Matched => "matched",
            PeopleSourceReferenceStatus.Ambiguous => "ambiguous",
            PeopleSourceReferenceStatus.Unattributed => "unattributed",
            _ => "not_found",
        };
        selectors.Add(new(index, selector.Kind, selector.Evidence, status, referenceResult.Issues));
        foreach (var candidate in referenceResult.Candidates)
        {
            variants.Add(
                new(
                    index,
                    new(endpoint.EvidenceId, selector.Evidence.Field, candidate.ReferenceUuid),
                    candidate.Name,
                    candidate.Attachment,
                    candidate.Projection,
                    candidate.Facts
                )
            );
        }
    }

    internal sealed record Result(
        PeopleAdjudicatedEndpointDiagnostic Diagnostic,
        PeopleActorProjectionResult? Projection
    );
}

/// <summary>A local UUID must always travel with its original source and field.</summary>
public sealed record PeopleSourceActorKey(string EvidenceId, string Field, Guid ReferenceUuid);

public sealed record PeopleAdjudicatedActorVariant(
    int SelectorIndex,
    PeopleSourceActorKey? ActorReference,
    string Name,
    PeopleSourceReferenceAttachment? Attachment,
    PeopleActorProjectionResult Projection,
    CreditedActorFacts<NormalizationTextEvidence>? Facts
);

public sealed record PeopleAdjudicatedSelectorDiagnostic(
    int SelectorIndex,
    string Kind,
    NormalizationTextEvidence Evidence,
    string Status,
    IReadOnlyList<string> Issues
);

public sealed record PeopleAdjudicatedEndpointDiagnostic(
    string EndpointId,
    string EvidenceId,
    Guid ArtistUuid,
    string Status,
    string? ProjectionIssue,
    IReadOnlyList<PeopleAdjudicatedSelectorDiagnostic> Selectors,
    IReadOnlyList<PeopleAdjudicatedActorVariant> Variants,
    string? MentionKind
);

public sealed record PeopleAdjudicatedGroupingBridge(
    PeopleSourceActorKey Reference,
    IReadOnlyList<string> EndpointIds,
    IReadOnlyList<string> TaskIds
);
