using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization;

public sealed partial class LunaCompositionHypothesisSelector
{
    private static JsonElement SupportSchema(JsonElement schema)
    {
        var enriched = JsonNode.Parse(schema.GetRawText())!.AsObject();
        enriched["properties"]!["support"] = JsonNode.Parse(
            """
            {"type":"object","properties":{
              "basis":{"type":"string","enum":["target_attribution","target_program_path","target_recording_path","background_only","none"]},
              "targetReferences":{"type":"array","maxItems":32,"items":{"type":"object","properties":{
                "witnessIndex":{"type":"integer","minimum":0},
                "claimIndex":{"type":["integer","null"],"minimum":0},
                "textIndex":{"type":["integer","null"],"minimum":0}},
                "required":["witnessIndex","claimIndex","textIndex"],"additionalProperties":false}},
              "recordingWorkPathReferences":{"type":"array","maxItems":32,"items":{"type":"object","properties":{
                "workMbid":{"type":"string"},"pathIndex":{"type":"integer","minimum":0}},
                "required":["workMbid","pathIndex"],"additionalProperties":false}}},
             "required":["basis","targetReferences","recordingWorkPathReferences"],"additionalProperties":false}
            """
        );
        enriched["required"]!.AsArray().Add("support");
        return JsonSerializer.SerializeToElement(enriched);
    }

    private static CompositionSelectionSupport ValidateSupport(
        CompositionHypothesisSelectionInput input,
        Guid? selected,
        JsonElement json
    )
    {
        RequireProperties(json, "basis", "recordingWorkPathReferences", "targetReferences");
        if (
            json.GetProperty("basis").ValueKind != JsonValueKind.String
            || json.GetProperty("targetReferences").ValueKind != JsonValueKind.Array
            || json.GetProperty("recordingWorkPathReferences").ValueKind != JsonValueKind.Array
        )
        {
            throw InvalidResponse();
        }
        foreach (var reference in json.GetProperty("targetReferences").EnumerateArray())
        {
            RequireProperties(reference, "claimIndex", "textIndex", "witnessIndex");
            RequireIndex(reference.GetProperty("witnessIndex"));
            RequireIndex(reference.GetProperty("claimIndex"), nullable: true);
            RequireIndex(reference.GetProperty("textIndex"), nullable: true);
        }
        foreach (var reference in json.GetProperty("recordingWorkPathReferences").EnumerateArray())
        {
            RequireProperties(reference, "pathIndex", "workMbid");
            RequireIndex(reference.GetProperty("pathIndex"));
            var work = reference.GetProperty("workMbid");
            if (work.ValueKind != JsonValueKind.String || !work.TryGetGuid(out _))
            {
                throw InvalidResponse();
            }
        }
        var support = json.Deserialize<CompositionSelectionSupport>(Json)!;
        if (
            support.Basis
                is not (
                    "target_attribution"
                    or "target_program_path"
                    or "target_recording_path"
                    or "background_only"
                    or "none"
                )
            || support.TargetReferences.Count > 32
            || support.RecordingWorkPathReferences.Count > 32
            || support.TargetReferences.Distinct().Count() != support.TargetReferences.Count
            || support.RecordingWorkPathReferences.Distinct().Count()
                != support.RecordingWorkPathReferences.Count
        )
        {
            throw InvalidResponse();
        }

        var context = input.TargetContext!;
        foreach (var reference in support.TargetReferences)
        {
            if (
                reference.WitnessIndex < 0
                || reference.WitnessIndex >= context.Witnesses.Count
                || reference.ClaimIndex.HasValue == reference.TextIndex.HasValue
            )
            {
                throw InvalidResponse();
            }
            var witness = context.Witnesses[reference.WitnessIndex];
            if (
                reference.ClaimIndex is { } claim && (claim < 0 || claim >= witness.Claims.Count)
                || reference.TextIndex is { } text && (text < 0 || text >= witness.Texts.Count)
            )
            {
                throw InvalidResponse();
            }
        }
        var paths = support
            .RecordingWorkPathReferences.Select(reference =>
            {
                var work = input.SuppliedCandidates.SingleOrDefault(work =>
                    work.WorkMbid == reference.WorkMbid
                );
                if (work == null)
                {
                    throw InvalidResponse();
                }
                var available = CompositionHypothesisPrompt.RecordingReleaseWorkPaths(work);
                if (reference.PathIndex < 0 || reference.PathIndex >= available.Count)
                {
                    throw InvalidResponse();
                }
                return (reference.WorkMbid, Path: available[reference.PathIndex]);
            })
            .ToArray();

        // A background choice is still retained as model output. Only a real,
        // target-owned source reference can permit a preferred shadow proposal.
        if (support.Basis is "background_only" or "none")
        {
            return support;
        }
        var selectedPaths = paths.Where(path => path.WorkMbid == selected).ToArray();
        if (!selected.HasValue || selectedPaths.Length == 0)
        {
            throw InvalidResponse();
        }
        var supported = support.TargetReferences.Any(reference =>
        {
            var witness = context.Witnesses[reference.WitnessIndex];
            return support.Basis switch
            {
                "target_attribution" => reference.ClaimIndex is { } claim
                    && selectedPaths.Any(path =>
                        CompositionHypothesisPrompt.MatchAttribution(
                            witness.Claims[claim],
                            path.Path
                        )
                            is not null
                    ),
                "target_program_path" => reference.TextIndex is { } text
                    && !string.IsNullOrWhiteSpace(witness.Texts[text].Value),
                "target_recording_path" => reference.ClaimIndex is { } claim
                    && IsTargetRecording(
                        witness.Claims[claim],
                        selectedPaths.Select(path => path.Path.Recording.Identity)
                    ),
                _ => false,
            };
        });
        if (!supported)
        {
            throw InvalidResponse();
        }
        return support;
    }

    private static bool IsTargetRecording(
        CompositionSelectionTargetClaim claim,
        IEnumerable<NormalizationResourceKey> recordings
    )
    {
        if (
            claim.Kind != "provider_recording_identity"
            || claim.Observation.ValueKind != JsonValueKind.Object
        )
        {
            return false;
        }
        var identity = claim.Observation.Deserialize<ObservedProviderIdentity>(Json)?.Identity;
        return identity is { Provider: "musicbrainz.org", ResourceType: "recording" }
            && Guid.TryParseExact(identity.ProviderKey, "D", out var uuid)
            && uuid != Guid.Empty
            && recordings.Contains(identity);
    }

    // Web defaults permit numeric strings. Model support must instead satisfy
    // the advertised integer/null schema before any DTO coercion can occur.
    private static void RequireIndex(JsonElement value, bool nullable = false)
    {
        if (nullable && value.ValueKind == JsonValueKind.Null)
        {
            return;
        }
        if (
            value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var index)
            || index < 0
        )
        {
            throw InvalidResponse();
        }
    }

    private static void RequireProperties(JsonElement value, params string[] expected)
    {
        if (
            value.ValueKind != JsonValueKind.Object
            || !value
                .EnumerateObject()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal)
                .SequenceEqual(expected)
        )
        {
            throw InvalidResponse();
        }
    }
}
