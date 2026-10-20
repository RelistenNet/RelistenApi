using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization;

/// <summary>
/// Projects complete retained semantic facts into a smaller model request. Only
/// equivalent facts are grouped; ordered nested values and unknown/null containers
/// remain intact. Citation examples are bounded independently of fact membership.
/// The original input hash binds every omitted citation to the reusable response.
/// </summary>
internal sealed class CompositionHypothesisPrompt
{
    internal const string Version = "composition-semantic-facts-v1";
    internal const string TargetContextVersion = "composition-semantic-target-context-v6";
    private const int MaximumCitationExamples = 2;
    private static readonly JsonSerializerOptions AttributionJson = new(JsonSerializerDefaults.Web);
    private readonly JsonSerializerOptions json;
    private readonly List<Citation> citations = [];
    private readonly Dictionary<string, int> citationIds = new(StringComparer.Ordinal);

    private CompositionHypothesisPrompt(JsonSerializerOptions json) => this.json = json;

    internal static object Build(
        CompositionHypothesisSelectionInput input,
        JsonSerializerOptions json
    )
    {
        var projection = new CompositionHypothesisPrompt(json);
        var works = input.SuppliedCandidates.Select(projection.Work).ToArray();
        var originalInputSha256 = Convert.ToHexStringLower(
            SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(input, json))
        );
        if (input.TargetContext is not null)
        {
            return new
            {
                representation = TargetContextVersion,
                originalInputSha256,
                song = input.Song,
                targetContext = CompositionTargetContextPrompt.Build(input.TargetContext, json),
                attributionRecordingWorkLinks = projection.Facts(
                    AttributionRecordingWorkLinks(input)
                ),
                recordingReleaseWorkPaths = input
                    .SuppliedCandidates.Select(work => new
                    {
                        work.WorkMbid,
                        paths = projection.Facts(RecordingReleaseWorkPaths(work)),
                    })
                    .ToArray(),
                suppliedCandidates = works,
                citationPool = projection.citations,
            };
        }
        // Keep the original v2 request representation byte-for-byte when no
        // context artifact was supplied; old cache entries retain their identity.
        return new
        {
            representation = Version,
            originalInputSha256,
            song = input.Song,
            suppliedCandidates = works,
            citationPool = projection.citations,
        };
    }

    private object Work(CompositionSelectionWork work) =>
        new
        {
            work.WorkMbid,
            work.EntityKey,
            identity = work.Identity.Identity,
            completeAuthorship = work.CompleteAuthorship,
            externalUniverseComplete = work.ExternalUniverseComplete,
            facts = new
            {
                titles = Facts(work.Titles),
                aliases = Facts(work.Aliases),
                iswcs = Facts(work.Iswcs),
                writerRoles = Facts(work.WriterRoles),
                lookupAssertions = Facts(work.LookupAssertions),
                searchAssertions = Facts(work.SearchAssertions),
                recordingWorkRelationships = Facts(work.RecordingWorkRelationships),
                recordingAssertions = Facts(work.RecordingAssertions),
            },
        };

    internal static IReadOnlyList<RecordingReleaseWorkPath> RecordingReleaseWorkPaths(
        CompositionSelectionWork work
    )
    {
        // This joins supplied provider assertions, not the target rendition. Keeping
        // the actual release→recording→work path together prevents separate fact
        // arrays from looking like unrelated title or background-author evidence.
        var relationships = work
            .RecordingWorkRelationships.Where(relationship =>
                IsMusicBrainzIdentity(relationship.Recording, "recording")
                && relationship.Work.Identity == work.Identity.Identity
                && relationship.Relationship.Type?.Value == "performance"
                && relationship.Relationship.Direction?.Value == "forward"
                && relationship.Relationship.TargetType?.Value == "work"
                && relationship.Relationship.Target?.Identity.Identity == work.Identity.Identity
            )
            .ToLookup(relationship => relationship.Recording.Identity);
        var paths = new List<RecordingReleaseWorkPath>();
        foreach (var assertion in work.SearchAssertions)
        {
            var hit = assertion.Hit;
            if (
                !IsMusicBrainzIdentity(hit.Entity.Identity, "recording")
                || hit.Releases is not { Count: > 0 } releases
            )
            {
                continue;
            }
            foreach (var relationship in relationships[hit.Entity.Identity.Identity])
            {
                paths.Add(
                    new(
                        paths.Count,
                        hit.Entity.Identity,
                        hit.Entity.Name,
                        hit.ArtistCredits,
                        releases,
                        relationship.Relationship,
                        relationship.Work,
                        assertion.Page,
                        hit.Evidence,
                        relationship.Relationship.Evidence
                    )
                );
            }
        }
        return paths;
    }

    private static IReadOnlyList<AttributionRecordingWorkLink> AttributionRecordingWorkLinks(
        CompositionHypothesisSelectionInput input
    )
    {
        var paths = input
            .SuppliedCandidates.SelectMany(work =>
                RecordingReleaseWorkPaths(work).Select(path => (work.WorkMbid, Path: path))
            )
            .ToArray();
        var links = new List<AttributionRecordingWorkLink>();
        var witnesses = input.TargetContext!.Witnesses;
        for (var witnessIndex = 0; witnessIndex < witnesses.Count; witnessIndex++)
        {
            var claims = witnesses[witnessIndex].Claims;
            for (var claimIndex = 0; claimIndex < claims.Count; claimIndex++)
            {
                var attribution = ReadAttribution(claims[claimIndex]);
                if (attribution is null)
                {
                    continue;
                }
                foreach (var path in paths)
                {
                    if (MatchAttribution(attribution, path.Path) is { } match)
                    {
                        links.Add(
                            new(
                                witnessIndex,
                                claimIndex,
                                path.WorkMbid,
                                path.Path.PathIndex,
                                match.Basis,
                                match.Annotation,
                                match.RecordingArtistCredit
                            )
                        );
                    }
                }
            }
        }
        return links;
    }

    internal static AttributionPerformerMatch? MatchAttribution(
        CompositionSelectionTargetClaim claim,
        RecordingReleaseWorkPath path
    )
    {
        var attribution = ReadAttribution(claim);
        return attribution is null ? null : MatchAttribution(attribution, path);
    }

    private static AttributionPerformer? ReadAttribution(CompositionSelectionTargetClaim claim)
    {
        if (
            claim.Kind != "performance_annotation"
            || claim.Observation.ValueKind != JsonValueKind.Object
        )
        {
            return null;
        }
        ProviderPerformanceAnnotation? annotation;
        try
        {
            annotation = claim.Observation.Deserialize<ProviderPerformanceAnnotation>(
                AttributionJson
            );
        }
        catch (JsonException)
        {
            return null;
        }
        if (annotation?.Kind is not ("cover_artist" or "original_performer"))
        {
            return null;
        }

        // A cover attribution names a performer, not a work writer. A shared
        // recording performer is the missing link to the observed performance
        // edge. Names are exact candidate labels, never canonical identities.
        var identities = (annotation.Identities ?? [])
            .Where(identity =>
                identity?.Identity
                    is { Provider: "musicbrainz.org", ResourceType: "artist" } resource
                && Guid.TryParseExact(resource.ProviderKey, "D", out var uuid)
                && uuid != Guid.Empty
            )
            .Select(identity => identity.Identity)
            .ToArray();
        var label = annotation.Name?.Value is { } name
            ? IdentityCandidateMatcher.PeopleNameCandidateKey(name)
            : "";
        return new(annotation, identities, label);
    }

    private static AttributionPerformerMatch? MatchAttribution(
        AttributionPerformer attribution,
        RecordingReleaseWorkPath path
    )
    {
        foreach (var credit in path.RecordingArtistCredits ?? [])
        {
            if (attribution.Identities.Length != 0)
            {
                if (
                    credit.Artist?.Identity?.Identity is { } identity
                    && attribution.Identities.Contains(identity)
                )
                {
                    return new(
                        "qualified_recording_performer_identity",
                        attribution.Annotation,
                        credit
                    );
                }
            }
            else if (
                attribution.Label.Length != 0
                && (
                    credit.Name?.Value is { } name
                        && IdentityCandidateMatcher.PeopleNameCandidateKey(name)
                            == attribution.Label
                    || credit.Artist?.Name?.Value is { } artistName
                        && IdentityCandidateMatcher.PeopleNameCandidateKey(artistName)
                            == attribution.Label
                )
            )
            {
                return new(
                    "exact_observed_recording_performer_label",
                    attribution.Annotation,
                    credit
                );
            }
        }
        return null;
    }

    private static bool IsMusicBrainzIdentity(ObservedProviderIdentity identity, string kind) =>
        identity.Identity.Provider == "musicbrainz.org"
        && identity.Identity.ResourceType == kind
        && Guid.TryParseExact(identity.Identity.ProviderKey, "D", out var uuid)
        && uuid != Guid.Empty;

    private IReadOnlyList<object> Facts<T>(IReadOnlyList<T> observations)
    {
        var facts = new Dictionary<string, Fact>(StringComparer.Ordinal);
        foreach (var observation in observations)
        {
            var sources = new HashSet<string>(StringComparer.Ordinal);
            var examples = new List<Citation>();
            var value = Strip(
                JsonSerializer.SerializeToElement(observation, json),
                sources,
                examples
            );
            var key = JsonSerializer.Serialize(value, json);
            if (!facts.TryGetValue(key, out var fact))
            {
                fact = new(value);
                facts.Add(key, fact);
            }
            foreach (var source in sources)
            {
                fact.Sources.Add(source);
            }
            foreach (var example in examples)
            {
                if (
                    fact.Examples.Count < MaximumCitationExamples
                    && !fact.Examples.Contains(example)
                )
                {
                    fact.Examples.Add(example);
                }
            }
        }
        return facts
            .Values.Select(fact =>
                (object)
                    new
                    {
                        value = fact.Value,
                        retainedSourcePositions = fact.Sources.Count,
                        representativeCitations = fact.Examples.Select(Intern).ToArray(),
                    }
            )
            .ToArray();
    }

    private object? Strip(JsonElement node, HashSet<string> sources, List<Citation> examples)
    {
        if (node.ValueKind == JsonValueKind.Array)
        {
            // Artist-credit joins and other nested sequences are source facts.
            // Group only complete top-level observations, never their positions.
            return node.EnumerateArray().Select(child => Strip(child, sources, examples)).ToArray();
        }
        if (node.ValueKind != JsonValueKind.Object)
        {
            return node.Clone();
        }
        var properties = node.EnumerateObject().ToArray();
        if (
            properties.Length == 2
            && node.TryGetProperty("evidence", out var evidence)
            && TryEvidence(evidence, out var reference)
        )
        {
            if (node.TryGetProperty("value", out var value))
            {
                Remember(new(reference!), sources, examples);
                return Strip(value, sources, examples);
            }
            if (node.TryGetProperty("identity", out var identity))
            {
                Remember(new(reference!), sources, examples);
                return Strip(identity, sources, examples);
            }
        }
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (
            var property in properties.OrderBy(property => property.Name, StringComparer.Ordinal)
        )
        {
            if (property.Name == "evidence" && TryEvidence(property.Value, out var observed))
            {
                Remember(new(observed!), sources, examples);
            }
            else if (
                property.Name is "origin" or "page"
                && property.Value.ValueKind == JsonValueKind.Object
                && property.Value.TryGetProperty("item", out var item)
                && property.Value.TryGetProperty("scope", out var scope)
                && property.Value.TryGetProperty("evidence", out var originEvidence)
                && TryEvidence(originEvidence, out var originReference)
            )
            {
                Remember(
                    new(
                        originReference!,
                        item.Deserialize<NormalizationResourceKey>(json),
                        scope.Clone()
                    ),
                    sources,
                    examples
                );
            }
            else
            {
                // In particular retain null versus empty/invalid containers;
                // omission must not turn unknown relationships into an empty set.
                result.Add(property.Name, Strip(property.Value, sources, examples));
            }
        }
        return result;
    }

    private bool TryEvidence(JsonElement node, out NormalizedEvidenceReference? evidence)
    {
        evidence = null;
        if (
            node.ValueKind != JsonValueKind.Object
            || !node.TryGetProperty("inputRevisionUuid", out var input)
            || input.ValueKind != JsonValueKind.String
            || !input.TryGetGuid(out var uuid)
            || uuid == Guid.Empty
            || !node.TryGetProperty("jsonPointer", out var pointer)
            || pointer.ValueKind != JsonValueKind.String
        )
        {
            return false;
        }
        evidence = node.Deserialize<NormalizedEvidenceReference>(json);
        return true;
    }

    private void Remember(Citation citation, HashSet<string> sources, List<Citation> examples)
    {
        var key = JsonSerializer.Serialize(citation.Evidence, json);
        if (sources.Add(key) && examples.Count < MaximumCitationExamples)
        {
            examples.Add(citation);
        }
    }

    private int Intern(Citation citation)
    {
        var key = JsonSerializer.Serialize(citation, json);
        if (!citationIds.TryGetValue(key, out var id))
        {
            id = citations.Count;
            citations.Add(citation);
            citationIds.Add(key, id);
        }
        return id;
    }

    private sealed record AttributionPerformer(
        ProviderPerformanceAnnotation Annotation,
        NormalizationResourceKey[] Identities,
        string Label
    );

    private sealed record AttributionRecordingWorkLink(
        int WitnessIndex,
        int ClaimIndex,
        Guid WorkMbid,
        int PathIndex,
        string MatchBasis,
        ProviderPerformanceAnnotation Annotation,
        MusicBrainzArtistCreditObservation RecordingArtistCredit
    );

    internal sealed record AttributionPerformerMatch(
        string Basis,
        ProviderPerformanceAnnotation Annotation,
        MusicBrainzArtistCreditObservation RecordingArtistCredit
    );

    internal sealed record RecordingReleaseWorkPath(
        int PathIndex,
        ObservedProviderIdentity Recording,
        ObservedValue<string>? RecordingName,
        IReadOnlyList<MusicBrainzArtistCreditObservation>? RecordingArtistCredits,
        IReadOnlyList<MusicBrainzSearchReleaseObservation> Releases,
        MusicBrainzRelationshipObservation PerformanceRelationship,
        ObservedProviderIdentity Work,
        ProviderObservationOrigin Page,
        NormalizedEvidenceReference MembershipEvidence,
        NormalizedEvidenceReference RelationshipEvidence
    );

    private sealed record Citation(
        NormalizedEvidenceReference Evidence,
        NormalizationResourceKey? Resource = null,
        JsonElement? ObservationScope = null
    );

    private sealed class Fact(object? value)
    {
        internal object? Value { get; } = value;
        internal HashSet<string> Sources { get; } = new(StringComparer.Ordinal);
        internal List<Citation> Examples { get; } = [];
    }
}
