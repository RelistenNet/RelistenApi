using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization.Batch;

/// <summary>Reads only the four authenticated development source artifacts, never answer targets or manifest inputs.</summary>
internal sealed class PeopleAdjudicatedSourcePackage
{
    private const long MaximumFileBytes = 64L * 1024 * 1024;
    private const long MaximumPackageBytes = 128L * 1024 * 1024;
    private const int MaximumRows = 100_000;
    private static readonly string[] SourcePaths =
    [
        "source/records.jsonl",
        "source/endpoints.jsonl",
        "source/questions.jsonl",
        "source/grouping.jsonl",
    ];
    private readonly Dictionary<string, (string Path, byte[] Bytes)> captured = new(
        StringComparer.Ordinal
    );

    public required string ManifestHash { get; init; }
    public required IReadOnlyDictionary<string, PeoplePairBaselineFile> Files { get; init; }
    public required IReadOnlyDictionary<string, Source> Sources { get; init; }
    public required IReadOnlyDictionary<string, Endpoint> Endpoints { get; init; }
    public required IReadOnlyList<Question> Questions { get; init; }
    public required IReadOnlyList<Grouping> Groups { get; init; }

    public sealed record Source(
        string EvidenceId,
        Guid ArtistUuid,
        IReadOnlyDictionary<string, string?> Fields
    );

    public sealed record Selector(string Kind, NormalizationTextEvidence Evidence);

    public sealed record Endpoint(
        string Id,
        string EvidenceId,
        Guid ArtistUuid,
        string? MentionKind,
        IReadOnlyList<Selector> Selectors
    );

    public sealed record Question(
        string Id,
        string Kind,
        string Focus,
        IReadOnlyList<string> Candidates,
        IReadOnlyList<string> SplitGroupIds
    );

    public sealed record Grouping(IReadOnlyList<string> TaskIds);

    public static async Task<PeopleAdjudicatedSourcePackage> ReadAsync(
        string packageDirectory,
        string expectedManifestSha256,
        CancellationToken ct
    )
    {
        var directory = Path.GetFullPath(packageDirectory);
        var manifestPath = Path.Combine(directory, "manifest.json");
        var manifestBytes = await ReadBytesAsync(manifestPath, ct);
        Require(
            IsHash(expectedManifestSha256) && Hash(manifestBytes) == expectedManifestSha256,
            "The externally pinned source package manifest changed."
        );

        using var document = JsonDocument.Parse(manifestBytes);
        var manifest = document.RootElement;
        AuthenticateManifest(manifest);
        var receipts = SourceReceipts(manifest);
        Preflight(directory, receipts, manifestBytes.LongLength);

        // The manifest may describe targets and upstream inputs. Only these fixed source paths
        // are eligible for IO; neither target availability nor target contents affect this reader.
        var bytes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var path in SourcePaths)
        {
            ct.ThrowIfCancellationRequested();
            var value = await ReadBytesAsync(Path.Combine(directory, path), ct);
            var receipt = receipts[path];
            Require(
                value.LongLength == receipt.Bytes && Hash(value) == receipt.Sha256,
                "An authenticated source artifact changed: " + path
            );
            bytes.Add(path, value);
        }

        var sources = ReadSources(bytes[SourcePaths[0]], receipts[SourcePaths[0]].Rows, ct);
        var endpoints = ReadEndpoints(
            bytes[SourcePaths[1]],
            receipts[SourcePaths[1]].Rows,
            sources,
            ct
        );
        var questions = ReadQuestions(
            bytes[SourcePaths[2]],
            receipts[SourcePaths[2]].Rows,
            endpoints,
            ct
        );
        var groups = ReadGroups(
            bytes[SourcePaths[3]],
            receipts[SourcePaths[3]].Rows,
            questions,
            ct
        );
        var files = new Dictionary<string, PeoplePairBaselineFile>(StringComparer.Ordinal)
        {
            ["manifest.json"] = new(Hash(manifestBytes), manifestBytes.LongLength, null),
        };
        foreach (var path in SourcePaths)
        {
            var receipt = receipts[path];
            files.Add(path, new(receipt.Sha256, receipt.Bytes, receipt.Rows));
        }
        var result = new PeopleAdjudicatedSourcePackage
        {
            ManifestHash = expectedManifestSha256,
            Files = files,
            Sources = sources.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Value,
                StringComparer.Ordinal
            ),
            Endpoints = endpoints,
            Questions = questions,
            Groups = groups,
        };
        result.captured.Add("manifest.json", (manifestPath, manifestBytes));
        foreach (var path in SourcePaths)
        {
            result.captured.Add(path, (Path.Combine(directory, path), bytes[path]));
        }
        return result;
    }

    /// <summary>Keep the export tied to the exact manifest and source bytes authenticated before projection.</summary>
    public async Task RecheckAsync(CancellationToken ct)
    {
        foreach (var (_, file) in captured)
        {
            Require(
                (await ReadBytesAsync(file.Path, ct)).AsSpan().SequenceEqual(file.Bytes),
                "An authenticated source input changed during export."
            );
        }
    }

    private static void AuthenticateManifest(JsonElement manifest)
    {
        Require(
            Text(manifest, "version") == "people-adjudicated-training-inputs:1"
                && Text(manifest, "state") == "prepared_source_selectors_and_separate_targets"
                && !manifest.GetProperty("eligible_for_release_evaluation").GetBoolean()
                && !manifest.GetProperty("identity_accuracy_established").GetBoolean()
                && !manifest.GetProperty("policy_activation_permitted").GetBoolean(),
            "Only the prepared development source package is supported."
        );
    }

    private sealed record Receipt(string Sha256, long Bytes, int Rows);

    private static Dictionary<string, Receipt> SourceReceipts(JsonElement manifest)
    {
        var result = new Dictionary<string, Receipt>(StringComparer.Ordinal);
        foreach (var artifact in manifest.GetProperty("artifacts").EnumerateArray())
        {
            var path = Text(artifact, "path");
            if (!SourcePaths.Contains(path, StringComparer.Ordinal))
            {
                Require(
                    !path.StartsWith("source/", StringComparison.Ordinal),
                    "The manifest declares an unsupported source artifact profile."
                );
                continue;
            }
            var receipt = new Receipt(
                Text(artifact, "sha256"),
                artifact.GetProperty("bytes").GetInt64(),
                artifact.GetProperty("rows").GetInt32()
            );
            Require(
                IsHash(receipt.Sha256)
                    && receipt.Bytes >= 0
                    && receipt.Bytes <= MaximumFileBytes
                    && receipt.Rows > 0
                    && receipt.Rows <= MaximumRows
                    && result.TryAdd(path, receipt),
                "A source artifact receipt is invalid or duplicated."
            );
        }
        Require(
            result.Count == SourcePaths.Length,
            "The manifest must authenticate all four fixed source artifacts."
        );
        return result;
    }

    private static void Preflight(
        string directory,
        IReadOnlyDictionary<string, Receipt> receipts,
        long manifestBytes
    )
    {
        RequirePlainPath(Path.Combine(directory, "source"));
        long total = manifestBytes;
        foreach (var path in SourcePaths)
        {
            var fullPath = Path.Combine(directory, path);
            RequirePlainPath(fullPath);
            Require(
                new FileInfo(fullPath).Length == receipts[path].Bytes,
                "A source artifact length differs from its manifest."
            );
            total += receipts[path].Bytes;
        }
        Require(total <= MaximumPackageBytes, "The authenticated source package exceeds 128 MiB.");
    }

    private sealed record SourceOwnership(Source Value, string FamilyGroup, string? KnownFamily);

    private static Dictionary<string, SourceOwnership> ReadSources(
        byte[] bytes,
        int expectedRows,
        CancellationToken ct
    )
    {
        var result = new Dictionary<string, SourceOwnership>(StringComparer.Ordinal);
        foreach (var row in Rows(bytes, expectedRows, ct))
        {
            var evidenceId = Text(row, "evidence_id");
            var raw = row.GetProperty("raw_record");
            var original = raw.GetProperty("row");
            var uuid = original.GetProperty("uuid").GetGuid();
            var artist = row.GetProperty("artist").GetProperty("uuid").GetGuid();
            var resource = row.GetProperty("resource");
            Require(
                uuid != Guid.Empty
                    && artist != Guid.Empty
                    && evidenceId == "source_sample:" + uuid.ToString("D")
                    && Text(raw, "kind") == "source_sample"
                    && Text(row, "artist_partition") == "development"
                    && Text(row, "family_partition") == "development"
                    && SupportedSourceProvider(row, resource)
                    && Text(resource, "resource_type") == "source"
                    && resource.GetProperty("resource_uuid").GetGuid() == uuid
                    && NullableText(raw, "family") == NullableText(row, "known_recording_family"),
                "A source row has incompatible development ownership."
            );
            var fields = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var property in original.EnumerateObject())
            {
                if (property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Null)
                {
                    Require(
                        fields.TryAdd(property.Name, property.Value.GetString()),
                        "A raw source field is duplicated."
                    );
                }
            }
            var source = new Source(evidenceId, artist, fields);
            Require(
                result.TryAdd(
                    evidenceId,
                    new(
                        source,
                        Text(row, "input_duplicate_family"),
                        NullableText(row, "known_recording_family")
                    )
                ),
                "A source evidence ID is duplicated."
            );
        }
        return result;
    }

    private static bool SupportedSourceProvider(JsonElement row, JsonElement resource)
    {
        if (Text(resource, "provider") == "relisten_catalog_snapshot")
        {
            return true;
        }
        if (
            Text(resource, "provider") != "relisten_retained_provider_field"
            || !row.TryGetProperty("provider_origin", out var origin)
            || origin.ValueKind != JsonValueKind.Object
        )
        {
            return false;
        }

        string[] required = ["provider", "kind", "evidence_sha256", "resource_key_sha256"];
        if (
            required.Any(name =>
                !origin.TryGetProperty(name, out var value)
                || value.ValueKind != JsonValueKind.String
            )
        )
        {
            return false;
        }

        // These hashes bind operator-retained provenance through the pinned package.
        // They do not establish upstream authenticity or accepted identity authority.
        return Text(origin, "provider") is "archive.org" or "local" or "phish.in"
            && Text(origin, "kind") == "retained_provider_input"
            && IsHash(Text(origin, "evidence_sha256"))
            && IsHash(Text(origin, "resource_key_sha256"));
    }

    private static Dictionary<string, Endpoint> ReadEndpoints(
        byte[] bytes,
        int expectedRows,
        IReadOnlyDictionary<string, SourceOwnership> sources,
        CancellationToken ct
    )
    {
        var result = new Dictionary<string, Endpoint>(StringComparer.Ordinal);
        foreach (var row in Rows(bytes, expectedRows, ct))
        {
            var id = Text(row, "endpoint_id");
            var evidenceId = Text(row, "evidence_id");
            Require(
                sources.TryGetValue(evidenceId, out var source),
                "An endpoint source is absent."
            );
            var artist = row.GetProperty("artist_uuid").GetGuid();
            Require(
                artist == source!.Value.ArtistUuid
                    && Text(row, "family_group") == source.FamilyGroup
                    && NullableText(row, "known_recording_family") == source.KnownFamily
                    && Text(row, "actor_reference_resolution") == "not_projected",
                "Endpoint ownership differs from its original source."
            );
            var mentionKind = NullableText(row, "mention_kind");
            var selectors = row.GetProperty("selectors");
            Require(
                selectors.GetArrayLength() is > 0 and <= 100,
                "An endpoint selector count is outside the supported bound."
            );
            var projected = selectors
                .EnumerateArray()
                .Select(selector => ReadSelector(selector, source.Value, mentionKind))
                .ToArray();
            Require(
                result.TryAdd(id, new(id, evidenceId, artist, mentionKind, projected)),
                "An endpoint ID is duplicated."
            );
        }
        return result;
    }

    private static Selector ReadSelector(JsonElement selector, Source source, string? mentionKind)
    {
        var kind = Text(selector, "kind");
        Require(
            kind switch
            {
                "actor_name" => mentionKind is null or "name",
                "source_expression" => mentionKind is "credit_expression" or "unresolved_reference",
                "source_contact_reference" or "source_url_reference" => mentionKind == null,
                _ => false,
            },
            "A selector kind is unsupported or disagrees with its mention kind."
        );
        var pointer = Text(selector, "field_path");
        const string prefix = "/raw_record/row/";
        Require(
            pointer.StartsWith(prefix, StringComparison.Ordinal),
            "A selector must cite a direct raw source field."
        );
        var encodedField = pointer[prefix.Length..];
        var field = encodedField
            .Replace("~1", "/", StringComparison.Ordinal)
            .Replace("~0", "~", StringComparison.Ordinal);
        source.Fields.TryGetValue(field, out var raw);
        Require(
            encodedField
                == field
                    .Replace("~", "~0", StringComparison.Ordinal)
                    .Replace("/", "~1", StringComparison.Ordinal)
                && !encodedField.Contains('/')
                && raw != null,
            "A selector pointer does not identify an original string field."
        );
        Require(
            Hash(Encoding.UTF8.GetBytes(raw!)) == Text(selector, "field_sha256"),
            "A selector field hash differs from its original raw field."
        );
        var span = selector.GetProperty("span");
        var start = span.GetProperty("start").GetInt32();
        var end = span.GetProperty("end").GetInt32();
        var quote = Text(span, "quote");
        Require(
            Text(span, "unit") == "unicode_code_points" && start >= 0 && end > start,
            "A selector requires a nonempty Unicode code point range."
        );
        Require(
            UnicodeSlice(raw!, start, end) == quote,
            "A selector is not the exact nonempty Unicode span of its original field."
        );
        // Authentication uses the package's raw-record coordinate. Projectors receive a
        // field-local pointer into Source.Fields, while the exact raw quote and offsets survive.
        return new(kind, new(field, "/" + encodedField, quote, start, end));
    }

    private static string UnicodeSlice(string raw, int start, int end)
    {
        var codePoint = 0;
        var offset = 0;
        var startOffset = -1;
        foreach (var rune in raw.EnumerateRunes())
        {
            if (codePoint == start)
            {
                startOffset = offset;
            }
            offset += rune.Utf16SequenceLength;
            if (++codePoint == end)
            {
                return raw[startOffset..offset];
            }
        }
        throw new InvalidDataException("A selector range extends beyond its original field.");
    }

    private static IReadOnlyList<Question> ReadQuestions(
        byte[] bytes,
        int expectedRows,
        IReadOnlyDictionary<string, Endpoint> endpoints,
        CancellationToken ct
    )
    {
        var result = new List<Question>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in Rows(bytes, expectedRows, ct))
        {
            var id = Text(row, "task_id");
            var kind = Text(row, "task_kind");
            var focus = Text(row, "focus_endpoint_id");
            var candidates = Strings(row, "candidate_endpoint_ids");
            var splitGroups = Strings(row, "split_group_ids");
            Require(
                ids.Add(id)
                    && endpoints.ContainsKey(focus)
                    && candidates.All(endpoints.ContainsKey)
                    && !candidates.Contains(focus, StringComparer.Ordinal)
                    && splitGroups.Length > 0
                    && (
                        Text(row, "candidate_scope")
                        is "original_validated_candidate_pool"
                            or "reviewed_declared_pairs"
                    ),
                "A question has invalid or incomplete endpoint references."
            );
            Require(
                kind switch
                {
                    "actor_kind" => candidates.Length == 0,
                    "cross_recording_identity" => candidates.Length > 0,
                    "within_recording_coreference" => candidates.Length > 0
                        && candidates.All(candidate =>
                            endpoints[candidate].EvidenceId == endpoints[focus].EvidenceId
                        ),
                    _ => false,
                },
                "A question kind disagrees with its complete candidate pool."
            );
            result.Add(new(id, kind, focus, candidates, splitGroups));
        }
        return result;
    }

    private static IReadOnlyList<Grouping> ReadGroups(
        byte[] bytes,
        int expectedRows,
        IReadOnlyList<Question> questions,
        CancellationToken ct
    )
    {
        var tasks = questions.ToDictionary(question => question.Id, StringComparer.Ordinal);
        var covered = new HashSet<string>(StringComparer.Ordinal);
        var splitOwners = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new List<Grouping>();
        foreach (var row in Rows(bytes, expectedRows, ct))
        {
            var ids = Strings(row, "task_ids");
            Require(ids.Length > 0, "A source grouping is empty.");
            foreach (var id in ids)
            {
                Require(
                    tasks.TryGetValue(id, out var task) && covered.Add(id),
                    "A grouped task is absent or appears more than once."
                );
                foreach (var splitGroup in task!.SplitGroupIds)
                {
                    Require(
                        !splitOwners.TryGetValue(splitGroup, out var owner)
                            || owner == result.Count,
                        "A shared source split group spans multiple groupings."
                    );
                    splitOwners[splitGroup] = result.Count;
                }
            }
            result.Add(new(ids));
        }
        Require(
            covered.Count == tasks.Count,
            "Source grouping does not cover every task exactly once."
        );
        return result;
    }

    private static IEnumerable<JsonElement> Rows(
        byte[] bytes,
        int expectedRows,
        CancellationToken ct
    )
    {
        var count = 0;
        using var reader = new StringReader(new UTF8Encoding(false, true).GetString(bytes));
        while (reader.ReadLine() is { } line)
        {
            ct.ThrowIfCancellationRequested();
            Require(
                !string.IsNullOrWhiteSpace(line) && ++count <= MaximumRows,
                "A source JSONL artifact has an empty row or exceeds 100,000 rows."
            );
            using var document = JsonDocument.Parse(line);
            yield return document.RootElement.Clone();
        }
        Require(
            count == expectedRows,
            "A source JSONL row count differs from its authenticated receipt."
        );
    }

    private static string[] Strings(JsonElement row, string name)
    {
        var values = row.GetProperty(name)
            .EnumerateArray()
            .Select(value =>
                value.GetString() ?? throw new InvalidDataException("A source ID is null.")
            )
            .ToArray();
        Require(
            values.All(value => !string.IsNullOrWhiteSpace(value))
                && values.Distinct(StringComparer.Ordinal).Count() == values.Length,
            "A source ID list contains an empty or repeated entry."
        );
        return values;
    }

    private static async Task<byte[]> ReadBytesAsync(string path, CancellationToken ct)
    {
        RequirePlainPath(Path.GetDirectoryName(path)!);
        RequirePlainPath(path);
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            true
        );
        Require(stream.Length <= MaximumFileBytes, "A source package file exceeds 64 MiB.");
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, ct);
        Require(
            await stream.ReadAsync(new byte[1], ct) == 0,
            "A source package file grew during authentication."
        );
        RequirePlainPath(Path.GetDirectoryName(path)!);
        RequirePlainPath(path);
        return bytes;
    }

    private static void RequirePlainPath(string path) =>
        Require(
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0,
            "Source package paths must not redirect through symbolic links."
        );

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool IsHash(string value) =>
        value.Length == 64
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string Text(JsonElement value, string name) =>
        value.GetProperty(name).GetString() is { Length: > 0 } text
            ? text
            : throw new InvalidDataException("A required source string is empty: " + name);

    private static string? NullableText(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind != JsonValueKind.Null
            ? property.GetString()
            : null;

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidDataException(message);
        }
    }
}
