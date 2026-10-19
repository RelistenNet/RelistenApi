using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization;

/// <summary>
/// Keeps every ordered target view while sharing exact text and provenance.
/// Only our known repertoire derivation loses matcher diagnostics; original
/// provider claims remain untouched and the caller hashes the complete input.
/// </summary>
internal sealed class CompositionTargetContextPrompt(JsonSerializerOptions json)
{
    private readonly List<SourceDocument> documents = [];
    private readonly Dictionary<SourceDocument, int> documentIds = [];
    private readonly List<SourcePosition> positions = [];
    private readonly Dictionary<SourcePosition, int> positionIds = [];
    private readonly List<object> texts = [];
    private readonly Dictionary<string, int> textIds = new(StringComparer.Ordinal);
    private readonly List<object> facts = [];
    private readonly Dictionary<string, int> factIds = new(StringComparer.Ordinal);

    internal static object Build(
        CompositionSelectionTargetContext context,
        JsonSerializerOptions json
    ) => new CompositionTargetContextPrompt(json).Project(context);

    private object Project(CompositionSelectionTargetContext context)
    {
        var artist = new
        {
            context.Artist.Name,
            context.Artist.MusicBrainzId,
            observations = context
                .Artist.Observations.Select(observation => new
                {
                    observation.Field,
                    observation.Value,
                    origin = Position(observation.Origin),
                })
                .ToArray(),
        };
        var witnesses = context.Witnesses.Select(Witness).ToArray();
        return new
        {
            context.Key,
            artist,
            context.Coverage,
            witnesses,
            sourceDocuments = documents,
            sourcePositions = positions,
            textPool = texts,
            derivedFacts = facts,
        };
    }

    private object Witness(CompositionSelectionTargetWitness witness) =>
        new
        {
            witness.Kind,
            origin = Position(witness.Origin),
            witness.FileUuid,
            witness.SourceUuid,
            witness.ShowUuid,
            witness.NativeFileKey,
            witness.RecordingKey,
            witness.SetlistKey,
            witness.Ordered,
            witness.MatchingRepertoireKeys,
            texts = witness
                .Texts.Select(text => new
                {
                    text.Field,
                    text = Text(text),
                    origin = Position(text.Origin),
                    text.Format,
                })
                .ToArray(),
            claims = witness.Claims.Select(Claim).ToArray(),
            witness.MissingFields,
        };

    private object Claim(CompositionSelectionTargetClaim claim) =>
        new
        {
            claim.Kind,
            claim.Role,
            claim.Value,
            observation = claim.Kind == "retained_repertoire_interpretation"
            && claim.Observation.ValueKind == JsonValueKind.Object
                ? DerivedInterpretation(claim.Observation)
                : (object)claim.Observation,
            origin = Position(claim.Origin),
        };

    private object DerivedInterpretation(JsonElement observation)
    {
        var ownership = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var interpretation = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in observation.EnumerateObject())
        {
            if (property.Name is "fileUuid" or "sourceUuid")
            {
                // These belong to this exact claim, not to a pooled fact. Keep
                // original missing/null values rather than borrowing witness IDs.
                ownership.Add(property.Name, property.Value);
            }
            else if (
                property.Name is "segments" or "wholeFileDictionaryCandidates"
                && property.Value.ValueKind == JsonValueKind.Array
            )
            {
                var omitted = property.Name == "segments" ? "comparisonDiagnostics" : "comparison";
                interpretation.Add(
                    property.Name,
                    property
                        .Value.EnumerateArray()
                        .Select(item => WithoutDiagnostic(item, omitted))
                        .ToArray()
                );
            }
            else
            {
                // Preserve semantic segments, alternatives, evidence, qualifiers,
                // unknown containers and future fields. Raw claim kinds never
                // enter this projection, even if they use the same field names.
                interpretation.Add(property.Name, property.Value);
            }
        }

        var fact = new { kind = "derived_repertoire_interpretation", observation = interpretation };
        var key = JsonSerializer.Serialize(fact, json);
        if (!factIds.TryGetValue(key, out var factId))
        {
            factId = facts.Count;
            facts.Add(fact);
            factIds.Add(key, factId);
        }
        return new { ownership, derivedFact = factId };
    }

    private static object WithoutDiagnostic(JsonElement item, string omitted) =>
        item.ValueKind == JsonValueKind.Object
            ? item.EnumerateObject()
                .Where(property => property.Name != omitted)
                .ToDictionary(
                    property => property.Name,
                    property => property.Value,
                    StringComparer.Ordinal
                )
            : item;

    private int Position(CompositionSelectionTargetOrigin origin)
    {
        var document = new SourceDocument(origin.Run, origin.Chunk, origin.Sha256);
        if (!documentIds.TryGetValue(document, out var documentId))
        {
            documentId = documents.Count;
            documents.Add(document);
            documentIds.Add(document, documentId);
        }
        var position = new SourcePosition(documentId, origin.Line, origin.JsonPointer);
        if (!positionIds.TryGetValue(position, out var positionId))
        {
            positionId = positions.Count;
            positions.Add(position);
            positionIds.Add(position, positionId);
        }
        return positionId;
    }

    private int Text(CompositionSelectionTargetText text)
    {
        if (!textIds.TryGetValue(text.TextSha256, out var textId))
        {
            textId = texts.Count;
            texts.Add(new { sha256 = text.TextSha256, text.Value });
            textIds.Add(text.TextSha256, textId);
        }
        return textId;
    }

    private sealed record SourceDocument(string Run, string Chunk, string Sha256);

    private sealed record SourcePosition(int Document, long Line, string JsonPointer);
}
