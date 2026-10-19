using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization.Batch;

/// <summary>
/// Reads completed, byte-pinned Luna evaluations as a separate shadow interpretation.
/// The caller supplies the exact extraction-stream hash; each selected source must also
/// reproduce its original artist and field map. This grants no publication authority.
/// </summary>
internal sealed class ShadowLunaCreditEvidence
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };
    private readonly Dictionary<Guid, SourceBinding> sources = new();

    internal string Identity { get; }

    internal ShadowLunaCreditEvidence(string directory, string expectedExtractionSha256)
    {
        if (!IsSha256(expectedExtractionSha256))
        {
            throw new ArgumentException(
                "Luna evidence requires the exact extraction stream SHA-256."
            );
        }
        directory = Path.GetFullPath(directory);
        var manifestBytes = File.ReadAllBytes(Path.Combine(directory, "manifest.json"));
        using var manifestDocument = JsonDocument.Parse(manifestBytes);
        var manifest = manifestDocument.RootElement;
        var manifestHash = Hash(manifestBytes);
        var selectionHash = manifest.GetProperty("selectionSha256").GetString();
        var resultsHash = manifest.GetProperty("resultsSha256").GetString();
        var inputHash = manifest.GetProperty("inputSha256").GetString();
        var version = manifest.GetProperty("extractorVersion").GetString();
        var job = manifest.GetProperty("jobUuid").GetGuid();
        if (
            manifest.GetProperty("state").GetString() is not ("completed" or "completed_partial")
            || !IsSha256(selectionHash)
            || !IsSha256(resultsHash)
            || !string.Equals(
                inputHash,
                expectedExtractionSha256,
                StringComparison.OrdinalIgnoreCase
            )
            || version != LunaRecordingCreditExtractor.Version
            || job == Guid.Empty
        )
        {
            throw new InvalidDataException(
                "Luna evidence needs a completed, pinned job for this exact extraction."
            );
        }

        var selectionBytes = File.ReadAllBytes(Path.Combine(directory, "selection.json"));
        if (!Hash(selectionBytes).Equals(selectionHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Luna selection differs from its completed manifest.");
        }
        var selection =
            JsonSerializer.Deserialize<LunaCreditEvaluationSelection>(selectionBytes, Json)
            ?? throw new InvalidDataException("Missing Luna selection.");
        if (
            !string.Equals(selection.InputSha256, inputHash, StringComparison.OrdinalIgnoreCase)
            || selection.Items.Count != manifest.GetProperty("records").GetInt32()
            || selection.Items.Count > 10000
        )
        {
            throw new InvalidDataException(
                "Luna selection and completion counts do not correspond."
            );
        }
        var selected = selection.Items.ToDictionary(item => item.Input.ResourceUuid);
        var states = new Dictionary<string, int>(StringComparer.Ordinal);
        using var hash = SHA256.Create();
        using var raw = File.OpenRead(Path.Combine(directory, "results.jsonl"));
        using var hashing = new CryptoStream(raw, hash, CryptoStreamMode.Read);
        using var reader = new StreamReader(hashing, Encoding.UTF8);
        int lineNumber = 0;
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            using var document = JsonDocument.Parse(line);
            var row = document.RootElement;
            var uuid = row.GetProperty("resourceUuid").GetGuid();
            var artist = row.GetProperty("artistUuid").GetGuid();
            var cohort = row.GetProperty("cohort").GetString();
            if (
                !selected.TryGetValue(uuid, out var item)
                || artist != item.Input.ArtistUuid
                || cohort != item.Cohort
                || item.Input.Kind != "source_sample"
                || uuid == Guid.Empty
                || artist == Guid.Empty
                || row.GetProperty("family").GetString() != item.Input.Family
                || !JsonElement.DeepEquals(
                    row.GetProperty("local"),
                    JsonSerializer.SerializeToElement(item.Input.CreditExtraction, Json)
                )
            )
            {
                throw new InvalidDataException(
                    "Luna result does not reproduce its selected source context."
                );
            }
            var result =
                row.GetProperty("luna").Deserialize<LunaCreditExtractionResult>(Json)
                ?? throw new InvalidDataException("Missing Luna result.");
            if (string.IsNullOrWhiteSpace(result.State))
            {
                throw new InvalidDataException("Missing Luna result state.");
            }
            if (result.State == "extracted")
            {
                ValidateCredits(item.Input.Inputs, result);
            }
            var basis = new ShadowLunaCreditBasis(
                job,
                version,
                result.CacheKey,
                cohort!,
                inputHash!,
                manifestHash,
                selectionHash!,
                resultsHash!,
                lineNumber
            );
            var interpretation = new ShadowLunaCreditMatch(
                result.State,
                result.Reason,
                basis,
                result.State == "extracted" ? result.Extraction : null,
                row.GetProperty("luna").Clone()
            );
            if (
                !sources.TryAdd(
                    uuid,
                    new(artist, item.Input.ArtistId, item.Input.Inputs, interpretation)
                )
            )
            {
                throw new InvalidDataException("A selected source has more than one Luna result.");
            }
            states[result.State] = states.GetValueOrDefault(result.State) + 1;
        }
        if (
            !Convert
                .ToHexStringLower(hash.Hash!)
                .Equals(resultsHash, StringComparison.OrdinalIgnoreCase)
            || sources.Count != selected.Count
            || !states
                .OrderBy(pair => pair.Key)
                .SequenceEqual(
                    manifest
                        .GetProperty("states")
                        .EnumerateObject()
                        .Select(property => new KeyValuePair<string, int>(
                            property.Name,
                            property.Value.GetInt32()
                        ))
                        .OrderBy(pair => pair.Key)
                )
        )
        {
            throw new InvalidDataException(
                "Luna result bytes or completion counts differ from their manifest."
            );
        }
        Identity = Hash(
            Encoding.UTF8.GetBytes(manifestHash + "\n" + selectionHash + "\n" + resultsHash)
        );
    }

    internal void ObserveRaw(JsonElement record)
    {
        var row = record.GetProperty("row");
        var uuid = row.GetProperty("uuid").GetGuid();
        if (sources.TryGetValue(uuid, out var saved))
        {
            sources[uuid] = saved with
            {
                RawFieldsMatch = saved.RawFieldsMatch != false && MatchesRaw(saved, row),
            };
        }
    }

    internal ShadowLunaCreditMatch? Resolve(
        NormalizationBatchExtraction extraction,
        JsonElement? actualRow = null
    )
    {
        if (!sources.TryGetValue(extraction.ResourceUuid, out var saved))
        {
            return null;
        }
        if (
            saved.ArtistUuid != extraction.ArtistUuid
            || !SameFields(saved.Inputs, extraction.Inputs)
            || saved.RawFieldsMatch == false
            || actualRow is { } row && !MatchesRaw(saved, row)
        )
        {
            // A stale selected row must not erase usable local extraction or poison the
            // global local-fact index. Keep the discrepancy visible without model claims.
            return saved.Interpretation with
            {
                State = "input_mismatch",
                Reason = "selected_artist_or_original_fields_changed",
                Extraction = null,
            };
        }
        return saved.Interpretation;
    }

    private static bool MatchesRaw(SourceBinding saved, JsonElement row)
    {
        if (row.TryGetProperty("artist_id", out var artist) && artist.GetInt64() != saved.ArtistId)
        {
            return false;
        }
        return saved.Inputs.All(pair =>
        {
            if (
                !row.TryGetProperty(pair.Key, out var value)
                || value.ValueKind == JsonValueKind.Null
            )
            {
                return pair.Value is null;
            }
            return value.ValueKind == JsonValueKind.String && value.GetString() == pair.Value;
        });
    }

    private static bool SameFields(
        IReadOnlyDictionary<string, string?> left,
        IReadOnlyDictionary<string, string?> right
    ) =>
        left.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .SequenceEqual(right.OrderBy(pair => pair.Key, StringComparer.Ordinal));

    internal static void ValidateCredits(
        IReadOnlyDictionary<string, string?> fields,
        LunaCreditExtractionResult result
    )
    {
        if (
            !IsSha256(result.CacheKey)
            || result.Extraction is null
            || result.Extraction.Unresolved.Count != 0
        )
        {
            throw new InvalidDataException(
                "An extracted Luna result lacks its validated cache identity."
            );
        }
        foreach (var credit in result.Extraction.Credits)
        {
            var evidence = credit.Evidence;
            if (
                credit.Role is not (RecordingCreditRole.Taper or RecordingCreditRole.Transferrer)
                || credit.ActorFacts is not null
                || credit.OriginalRole is not null
                || string.IsNullOrWhiteSpace(credit.Name)
                || credit.Name != credit.Name.Trim()
                || credit.Name.Length > 100
                || !credit.Name.Any(char.IsLetter)
                || credit.Name.Contains('\n')
                || Regex.IsMatch(credit.Name, @"[\p{L}\p{N}._%+\-]+@[\p{L}\p{N}.\-]+\.[\p{L}]{2,}")
                || evidence is null
                || !fields.TryGetValue(evidence.Field, out var text)
                || text is null
                || evidence.JsonPointer
                    != "/" + evidence.Field.Replace("~", "~0").Replace("/", "~1")
                || !evidence.Quote.Contains(credit.Name, StringComparison.Ordinal)
            )
            {
                throw new InvalidDataException(
                    "Luna credit violates its retained name, role or evidence contract."
                );
            }
            var runes = text.EnumerateRunes().ToArray();
            if (
                evidence.StartCodePoint < 0
                || evidence.EndCodePoint < evidence.StartCodePoint
                || evidence.EndCodePoint > runes.Length
                || string.Concat(
                    runes[evidence.StartCodePoint..evidence.EndCodePoint]
                        .Select(rune => rune.ToString())
                ) != evidence.Quote
            )
            {
                throw new InvalidDataException(
                    "Luna credit does not cite the exact original Unicode span."
                );
            }
        }
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed record SourceBinding(
        Guid ArtistUuid,
        long ArtistId,
        IReadOnlyDictionary<string, string?> Inputs,
        ShadowLunaCreditMatch Interpretation,
        bool? RawFieldsMatch = null
    );
}

/// <summary>Exact retained job coordinates; this is extraction evidence, not person identity authority.</summary>
public sealed record ShadowLunaCreditBasis(
    Guid JobUuid,
    string ExtractorVersion,
    string? CacheKey,
    string Cohort,
    string InputSha256,
    string ManifestSha256,
    string SelectionSha256,
    string ResultsSha256,
    int ResultLineNumber
);

internal sealed record ShadowLunaCreditMatch(
    string State,
    string? Reason,
    ShadowLunaCreditBasis Basis,
    RecordingCreditExtraction? Extraction,
    JsonElement OriginalResult
);

/// <summary>Model outcomes remain separate from local extraction, including empty and failed outcomes.</summary>
public sealed record ShadowLunaCreditInterpretation(
    string State,
    string? Reason,
    ShadowLunaCreditBasis Basis,
    IReadOnlyList<string> PersonEntityKeys,
    IReadOnlyList<string> CreditEntityKeys
);
