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
    internal const string TargetContextVersion = "composition-semantic-target-context-v1";
    private const int MaximumCitationExamples = 2;
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
                targetContext = TargetContext(input.TargetContext),
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

    private static object TargetContext(CompositionSelectionTargetContext context)
    {
        // The same original description can support many file views. Retain each
        // view's ownership/citation, but send its identical literal only once for
        // this target. No text from another target's export pool enters the prompt.
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        var witnesses = context
            .Witnesses.Select(witness => new
            {
                witness.Kind,
                witness.Origin,
                witness.FileUuid,
                witness.SourceUuid,
                witness.ShowUuid,
                witness.NativeFileKey,
                witness.RecordingKey,
                witness.SetlistKey,
                witness.Ordered,
                witness.MatchingRepertoireKeys,
                texts = witness
                    .Texts.Select(text =>
                    {
                        texts[text.TextSha256] = text.Value;
                        return new
                        {
                            text.Field,
                            text.TextSha256,
                            text.Origin,
                            text.Format,
                        };
                    })
                    .ToArray(),
                witness.Claims,
                witness.MissingFields,
            })
            .ToArray();
        return new
        {
            context.Key,
            context.Artist,
            context.Coverage,
            witnesses,
            textPool = texts
                .Select(text => new { sha256 = text.Key, value = text.Value })
                .ToArray(),
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
