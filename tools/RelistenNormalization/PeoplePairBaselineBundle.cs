using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Relisten.Normalization.Batch;

/// <summary>Authenticates development selection before any mention or selected source text is decoded.</summary>
internal sealed class PeoplePairBaselineBundle
{
    private const int MaximumFileBytes = 64 * 1024 * 1024;
    private const string Annotation =
        "local-dev/normalization-data/2026-10-17-cross-artist/annotation-v1/evidence.jsonl";
    private readonly Dictionary<string, (string Path, byte[] Bytes)> captured = new(
        StringComparer.Ordinal
    );
    public Dictionary<string, PeoplePairBaselineFile> Files { get; } = new(StringComparer.Ordinal);
    public required string ManifestHash { get; init; }
    public required IReadOnlyDictionary<string, Mention> Mentions { get; init; }
    public required IReadOnlyList<Question> Questions { get; init; }

    public static async Task<PeoplePairBaselineBundle> ReadAsync(
        string root,
        string bundle,
        string expectedHash,
        CancellationToken ct
    )
    {
        var manifestBytes = await Bytes(Path.Combine(bundle, "manifest.json"), ct);
        Require(
            expectedHash.Length == 64 && Hash(manifestBytes) == expectedHash,
            "The frozen bundle manifest changed."
        );
        using var manifestDocument = JsonDocument.Parse(manifestBytes);
        var manifest = manifestDocument.RootElement;
        Require(
            Text(manifest, "usage") == "development_only"
                && !manifest.GetProperty("policy_activation_permitted").GetBoolean()
                && !manifest.GetProperty("identity_accuracy_established").GetBoolean(),
            "Only a development-only bundle is supported."
        );
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["manifest.json"] = manifestBytes,
        };
        var rootFiles = new HashSet<string>(StringComparer.Ordinal);
        Require(
            manifest.GetProperty("files").EnumerateObject().Count() <= 32,
            "The bundle exceeds 32 tracked input files."
        );
        foreach (var property in manifest.GetProperty("files").EnumerateObject())
        {
            var path = Below(bundle, property.Name);
            RequireCapacity(files, property.Value.GetProperty("bytes").GetInt64());
            files.Add(property.Name, await Authenticated(path, property.Value, ct));
        }
        foreach (var name in new[] { "reservation_index", "original_selected_evidence" })
        {
            var receipt = manifest.GetProperty(name);
            var relative = Text(receipt, "path");
            RequireCapacity(files, receipt.GetProperty("bytes").GetInt64());
            files.Add(relative, await Authenticated(Below(root, relative), receipt, ct));
            rootFiles.Add(relative);
        }
        RequireCapacity(files, new FileInfo(Below(root, Annotation)).Length);
        var annotation = await Bytes(Below(root, Annotation), ct);
        Require(
            Hash(annotation) == Text(manifest, "source_annotation_evidence_sha256"),
            "The original annotation evidence changed."
        );
        files.Add(Annotation, annotation);
        rootFiles.Add(Annotation);
        using var reservationsDocument = JsonDocument.Parse(
            files[Text(manifest.GetProperty("reservation_index"), "path")]
        );
        using var reservationReceipt = JsonDocument.Parse(files["reservation-receipt.json"]);
        Require(
            reservationReceipt.RootElement.GetProperty("metadata_only_projection").GetBoolean()
                && !reservationReceipt.RootElement.GetProperty("seed_labels_read").GetBoolean()
                && JsonElement.DeepEquals(
                    reservationReceipt.RootElement.GetProperty("index"),
                    manifest.GetProperty("reservation_index")
                ),
            "The reservation provenance differs from the frozen bundle."
        );
        var sourceIndex = Rows(files["source-index.jsonl"])
            .ToDictionary(value => Text(value, "evidence_id"), StringComparer.Ordinal);
        AuthenticateSelection(sourceIndex, reservationsDocument.RootElement);

        // Selection metadata and original byte hashes are established before parsing any raw
        // annotation payload. Nonselected physical lines are never decoded, including malformed UTF-8.
        var originals = ReadSelectedOriginals(sourceIndex, annotation, ct);
        var citations = ReadCitations(files["citations.jsonl"], originals, ct);
        var mentions = ReadMentions(files["mentions.jsonl"], sourceIndex, citations);
        AuthenticatePool(files["candidate-pool.json"], files["mentions.jsonl"], mentions);
        var questions = ReadQuestions(files["labels.jsonl"], mentions);
        var result = new PeoplePairBaselineBundle
        {
            ManifestHash = expectedHash,
            Mentions = mentions,
            Questions = questions,
        };
        foreach (var (name, bytes) in files)
        {
            result.captured.Add(
                name,
                (Below(rootFiles.Contains(name) ? root : bundle, name), bytes)
            );
            result.Files.Add(
                name,
                new(
                    Hash(bytes),
                    bytes.LongLength,
                    name.EndsWith(".jsonl", StringComparison.Ordinal) ? CountLines(bytes) : null
                )
            );
        }
        return result;
    }

    /// <summary>Reject a concurrent input replacement rather than mixing authenticated and later bytes.</summary>
    public async Task RecheckAsync(CancellationToken ct)
    {
        foreach (var (_, capturedFile) in captured)
        {
            Require(
                (await Bytes(capturedFile.Path, ct)).AsSpan().SequenceEqual(capturedFile.Bytes),
                "An authenticated input changed during export."
            );
        }
    }

    private static void AuthenticateSelection(
        IReadOnlyDictionary<string, JsonElement> selected,
        JsonElement index
    )
    {
        var eligible = index
            .GetProperty("eligible_source_metadata")
            .EnumerateArray()
            .ToDictionary(value => Text(value, "evidence_id"), StringComparer.Ordinal);
        var excluded = index
            .GetProperty("excluded_annotation_metadata")
            .EnumerateArray()
            .Select(value => Text(value, "evidence_id"))
            .ToHashSet(StringComparer.Ordinal);
        var groups = index
            .GetProperty("reserved_annotation_family_groups")
            .EnumerateArray()
            .Select(value => value.GetString())
            .ToHashSet(StringComparer.Ordinal);
        var families = index
            .GetProperty("reserved_known_families")
            .EnumerateArray()
            .Select(value => value.GetString())
            .ToHashSet(StringComparer.Ordinal);
        Require(
            selected.Count > 0 && selected.Count <= 10000,
            "The selected source count exceeds the development profile."
        );
        foreach (var (id, source) in selected)
        {
            Require(
                eligible.TryGetValue(id, out var allowed) && !excluded.Contains(id),
                "A selected source is reserved or not eligible development data."
            );
            Require(
                Text(source, "artist_partition") == "development"
                    && Text(source, "family_partition") == "development"
                    && Text(allowed, "artist_partition") == "development"
                    && Text(allowed, "family_partition") == "development",
                "Held-out source selection is forbidden."
            );
            Require(
                !groups.Contains(Text(source, "family_group"))
                    && !families.Contains(NullableText(source, "known_recording_family"))
                    && Text(source, "family_group") == Text(allowed, "family_group")
                    && NullableText(source, "known_recording_family")
                        == NullableText(allowed, "family")
                    && source.GetProperty("artist").GetProperty("uuid").GetGuid()
                        == allowed.GetProperty("artist_uuid").GetGuid()
                    && source.GetProperty("annotation_line_number").GetInt32()
                        == allowed.GetProperty("source_line").GetInt32()
                    && Text(allowed, "reference")
                        == "source:" + source.GetProperty("source_id").GetInt32(),
                "Selected source metadata does not match reservation-filtered ownership."
            );
        }
    }

    private static Dictionary<string, JsonElement> ReadSelectedOriginals(
        IReadOnlyDictionary<string, JsonElement> sources,
        byte[] bytes,
        CancellationToken ct
    )
    {
        var wanted = sources.ToDictionary(
            pair => pair.Value.GetProperty("annotation_line_number").GetInt32(),
            pair => pair.Key
        );
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (number, line) in Lines(bytes))
        {
            ct.ThrowIfCancellationRequested();
            if (!wanted.TryGetValue(number, out var id))
            {
                continue;
            }
            var metadata = sources[id];
            Require(
                Hash(line.Span) == Text(metadata, "annotation_line_sha256"),
                "An original selected line changed."
            );
            using var document = JsonDocument.Parse(line);
            var record = document.RootElement;
            var row = record.GetProperty("raw_record").GetProperty("row");
            Require(
                Text(record, "evidence_id") == id
                    && Text(record, "artist_partition") == "development"
                    && Text(record, "family_partition") == "development"
                    && Text(record, "input_duplicate_family") == Text(metadata, "family_group")
                    && NullableText(record, "known_recording_family")
                        == NullableText(metadata, "known_recording_family")
                    && JsonElement.DeepEquals(
                        record.GetProperty("artist"),
                        metadata.GetProperty("artist")
                    )
                    && Text(record.GetProperty("raw_record"), "kind") == "source_sample"
                    && row.GetProperty("id").GetInt32()
                        == metadata.GetProperty("source_id").GetInt32()
                    && row.GetProperty("uuid").GetGuid()
                        == metadata.GetProperty("source_uuid").GetGuid(),
                "Original source ownership or development metadata changed."
            );
            result.Add(id, record.Clone());
        }
        Require(
            result.Count == sources.Count,
            "The original annotation is missing selected physical lines."
        );
        return result;
    }

    private static Dictionary<string, JsonElement> ReadCitations(
        byte[] bytes,
        IReadOnlyDictionary<string, JsonElement> originals,
        CancellationToken ct
    )
    {
        var citations = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var citation in Rows(bytes))
        {
            ct.ThrowIfCancellationRequested();
            var id = Text(citation, "citation_id");
            Require(
                originals.TryGetValue(Text(citation, "evidence_id"), out var original),
                "A citation references an unselected source."
            );
            var path = Text(citation, "field_path");
            Require(
                path.StartsWith("/raw_record/row/", StringComparison.Ordinal)
                    && path.Count(value => value == '/') == 3,
                "Unsupported original citation field."
            );
            var text = original
                .GetProperty("raw_record")
                .GetProperty("row")
                .GetProperty(path.Split('/')[^1])
                .GetString()!;
            var span = citation.GetProperty("span");
            var start = span.GetProperty("start").GetInt32();
            var end = span.GetProperty("end").GetInt32();
            var runes = text.EnumerateRunes().ToArray();
            Require(
                Hash(Encoding.UTF8.GetBytes(text)) == Text(citation, "field_sha256")
                    && Text(span, "unit") == "unicode_code_points"
                    && start >= 0
                    && start < end
                    && end <= runes.Length
                    && string.Concat(
                        runes.Skip(start).Take(end - start).Select(value => value.ToString())
                    ) == Text(span, "quote"),
                "An exact Unicode source citation does not reproduce retained bytes."
            );
            citations.Add(id, citation);
        }
        return citations;
    }

    private static Dictionary<string, Mention> ReadMentions(
        byte[] bytes,
        IReadOnlyDictionary<string, JsonElement> sources,
        IReadOnlyDictionary<string, JsonElement> citations
    )
    {
        var mentions = new Dictionary<string, Mention>(StringComparer.Ordinal);
        foreach (var row in Rows(bytes))
        {
            var id = Text(row, "mention_id");
            var evidence = Text(row, "evidence_id");
            Require(
                sources.TryGetValue(evidence, out var source),
                "A mention lacks selected source evidence."
            );
            Require(
                citations.TryGetValue(Text(row, "citation_id"), out var citation),
                "A mention lacks a retained source citation."
            );
            Require(
                Text(citation, "evidence_id") == evidence
                    && Text(row, "name") == Text(citation.GetProperty("span"), "quote")
                    && JsonElement.DeepEquals(
                        row.GetProperty("artist"),
                        source.GetProperty("artist")
                    ),
                "A mention changed its exact source wording or artist."
            );
            var kind = Text(row, "mention_kind");
            Require(
                kind is "name" or "credit_expression" or "unresolved_reference",
                "Unknown source mention expression kind."
            );
            mentions.Add(
                id,
                new(
                    id,
                    evidence,
                    Text(row, "name"),
                    row.GetProperty("artist").GetProperty("uuid").GetGuid(),
                    kind
                )
            );
        }
        return mentions;
    }

    private static void AuthenticatePool(
        byte[] bytes,
        byte[] mentionsBytes,
        IReadOnlyDictionary<string, Mention> mentions
    )
    {
        using var document = JsonDocument.Parse(bytes);
        var pool = document.RootElement;
        var keys = pool.GetProperty("mention_ids")
            .EnumerateArray()
            .Select(value => value.GetString()!)
            .ToArray();
        Require(
            keys.Length == mentions.Count
                && keys.Distinct(StringComparer.Ordinal).Count() == keys.Length
                && keys.ToHashSet(StringComparer.Ordinal).SetEquals(mentions.Keys)
                && Text(pool, "mentions_file_sha256") == Hash(mentionsBytes)
                && !pool.GetProperty("identity_assertion").GetBoolean()
                && !pool.GetProperty("matcher_feature").GetBoolean()
                && !pool.GetProperty("creation_authorized").GetBoolean(),
            "The frozen mention inventory changed or claims identity authority."
        );
    }

    private static IReadOnlyList<Question> ReadQuestions(
        byte[] bytes,
        IReadOnlyDictionary<string, Mention> mentions
    )
    {
        var questions = new List<Question>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var task in Rows(bytes))
        {
            var id = Text(task, "task_id");
            var kind = Text(task, "task_kind");
            Require(
                seen.Add(id)
                    && Text(task, "status") == "draft_development"
                    && !task.GetProperty("evaluation_eligible").GetBoolean()
                    && !task.GetProperty("policy_activation_permitted").GetBoolean(),
                "Only frozen development questions are supported."
            );
            var focus = Text(task, "focus_mention_id");
            Require(
                mentions.ContainsKey(focus)
                    && kind
                        is (
                            "actor_kind"
                            or "within_recording_coreference"
                            or "cross_recording_identity"
                        ),
                "Unknown question kind or focal mention."
            );
            var candidates =
                kind == "actor_kind"
                    ? []
                    : task.GetProperty("candidate_mention_ids")
                        .EnumerateArray()
                        .Select(value => value.GetString()!)
                        .ToArray();
            Require(
                candidates.Distinct(StringComparer.Ordinal).Count() == candidates.Length
                    && candidates.All(key => key != focus && mentions.ContainsKey(key)),
                "Declared candidate pool contains duplicate or missing mentions."
            );
            questions.Add(new(id, kind, focus, candidates));
        }
        return questions;
    }

    private static void RequireCapacity(
        IReadOnlyDictionary<string, byte[]> files,
        long incomingBytes
    ) =>
        Require(
            incomingBytes >= 0
                && files.Values.Sum(value => (long)value.Length) + incomingBytes
                    <= 128L * 1024 * 1024,
            "The complete bundle exceeds 128 MiB."
        );

    private static async Task<byte[]> Authenticated(
        string path,
        JsonElement receipt,
        CancellationToken ct
    )
    {
        var bytes = await Bytes(path, ct);
        Require(
            bytes.LongLength == receipt.GetProperty("bytes").GetInt64()
                && Hash(bytes) == Text(receipt, "sha256"),
            "An immutable bundle input changed."
        );
        return bytes;
    }

    private static async Task<byte[]> Bytes(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        Require(stream.Length <= MaximumFileBytes, "An input exceeds the 64 MiB per-file profile.");
        var bytes = new byte[checked((int)stream.Length + 1)];
        var length = 0;
        while (length < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(length), ct);
            if (read == 0)
            {
                break;
            }
            length += read;
        }
        Require(length < bytes.Length, "An input grew while its bounded snapshot was read.");
        return bytes.AsSpan(0, length).ToArray();
    }

    private static string Below(string root, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative));
        Require(
            path.StartsWith(
                Path.GetFullPath(root) + Path.DirectorySeparatorChar,
                StringComparison.Ordinal
            ),
            "Input path escapes its authenticated root."
        );
        return path;
    }

    private static IEnumerable<JsonElement> Rows(byte[] bytes)
    {
        foreach (var (_, line) in Lines(bytes))
        {
            using var document = JsonDocument.Parse(line);
            yield return document.RootElement.Clone();
        }
    }

    private static IEnumerable<(int Number, ReadOnlyMemory<byte> Line)> Lines(byte[] bytes)
    {
        var start = 0;
        var number = 0;
        for (var index = 0; index <= bytes.Length; index++)
        {
            if (index != bytes.Length && bytes[index] != (byte)'\n')
            {
                continue;
            }
            if (index > start)
            {
                var end = index;
                while (end > start && bytes[end - 1] == (byte)'\r')
                {
                    end--;
                }
                yield return (++number, bytes.AsMemory(start, end - start));
            }
            else if (index < bytes.Length)
            {
                number++;
            }
            start = index + 1;
        }
    }

    private static int CountLines(byte[] bytes) => Lines(bytes).Count();

    internal static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string Text(JsonElement value, string key) =>
        value.GetProperty(key).GetString()!;

    private static string? NullableText(JsonElement value, string key) =>
        value.GetProperty(key).ValueKind == JsonValueKind.Null ? null : Text(value, key);

    internal static void Require(bool condition, string reason)
    {
        if (!condition)
        {
            throw new InvalidDataException(reason);
        }
    }

    internal sealed record Mention(
        string Id,
        string EvidenceId,
        string Name,
        Guid ArtistUuid,
        string Kind
    );

    internal sealed record Question(
        string Id,
        string Kind,
        string Focus,
        IReadOnlyList<string> Candidates
    );
}
