using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization.Batch;

/// <summary>
/// Exports the actual pure venue matcher over retained development names. Draft decisions,
/// primary-source rationales and split links never enter the matcher projection. Raw geography
/// does not establish resolved locality, address, kind, parent or authoritative identifiers.
/// </summary>
public sealed class VenuePairBaselineBatch
{
    public const string Version = "venue-pair-baseline-v1";
    public const string ProjectionVersion = "venue-retained-names-only-v1";
    private const int MaximumPairs = 4096;
    private const int MaximumEvidence = 16384;
    private const int MaximumLineCharacters = 65536;
    private static readonly (string Name, int Bytes, int Rows)[] BundleFiles =
    [
        ("labels.jsonl", 4 * 1024 * 1024, MaximumPairs),
        ("evidence.jsonl", 16 * 1024 * 1024, MaximumEvidence),
        ("split-links.jsonl", 1024 * 1024, MaximumPairs),
    ];
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    /// <summary>
    /// Checks the bundle manifest and compares each pair without a matching policy. A new output
    /// directory retains a partial prefix on failure; only a fully flushed run publishes manifest.json.
    /// The manifest fingerprints the exact bytes read and the assemblies actually executing the matcher.
    /// </summary>
    public async Task<VenuePairBaselineReport> RunAsync(
        string bundleDirectory,
        string outputDirectory,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        bundleDirectory = Path.GetFullPath(bundleDirectory);
        outputDirectory = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(outputDirectory) || File.Exists(outputDirectory))
        {
            throw new IOException("Choose a new venue baseline output directory.");
        }
        var manifestBytes = await ReadBounded(
            Path.Combine(bundleDirectory, "manifest.json"),
            1024 * 1024,
            cancellationToken
        );
        var declaredFiles = ReadManifest(manifestBytes);
        var inputs = new Dictionary<string, byte[]>();
        var fingerprints = new Dictionary<string, VenuePairBaselineFile>();
        foreach (var (name, bound, maximumRows) in BundleFiles)
        {
            var bytes = await ReadBounded(
                Path.Combine(bundleDirectory, name),
                bound,
                cancellationToken
            );
            var rows = Lines(bytes).Count();
            var hash = Hash(bytes);
            var expected = declaredFiles[name];
            if (
                rows > maximumRows
                || rows != expected.Rows
                || bytes.LongLength != expected.Bytes
                || hash != expected.Sha256
            )
            {
                throw new InvalidDataException(
                    "Venue bundle file does not match its manifest or bounds."
                );
            }
            inputs.Add(name, bytes);
            fingerprints.Add(name, new(hash, bytes.LongLength, rows));
        }
        // Links are fingerprinted for the training receipt, not parsed or supplied as features.
        var venues = ReadVenues(inputs["evidence.jsonl"], cancellationToken);
        Directory.CreateDirectory(outputDirectory);
        var partialPath = Path.Combine(outputDirectory, "baseline.partial.jsonl");
        var count = await WriteComparisons(
            inputs["labels.jsonl"],
            venues,
            partialPath,
            cancellationToken
        );
        cancellationToken.ThrowIfCancellationRequested();
        var outputHash = await HashFile(partialPath, cancellationToken);
        var report = new VenuePairBaselineReport(
            "completed",
            Version,
            ProjectionVersion,
            Hash(manifestBytes),
            fingerprints,
            await HashFile(typeof(IdentityCandidateMatcher).Assembly.Location, cancellationToken),
            await HashFile(typeof(VenuePairBaselineBatch).Assembly.Location, cancellationToken),
            "baseline.jsonl",
            outputHash,
            count,
            [
                "Only retained name wording is projected; raw location and coordinates are not resolved geographic facts.",
                "Blank kind, null parent/date/UUID and no authoritative identifiers represent unknown context.",
                "Empty LocationAssertions means no supported resolved geographic facts are available, not that the venue has no location.",
                "No policy is supplied. Comparisons are experimental proposals, not accepted identities or evaluation accuracy.",
            ]
        );
        var manifestPath = Path.Combine(outputDirectory, "manifest.partial.json");
        await using (
            var stream = new FileStream(
                manifestPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read
            )
        )
        {
            await JsonSerializer.SerializeAsync(stream, report, Json, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        File.Move(partialPath, Path.Combine(outputDirectory, "baseline.jsonl"));
        File.Move(manifestPath, Path.Combine(outputDirectory, "manifest.json"));
        return report;
    }

    private static Dictionary<string, VenueMatchRecord> ReadVenues(
        byte[] bytes,
        CancellationToken ct
    )
    {
        var result = new Dictionary<string, VenueMatchRecord>(StringComparer.Ordinal);
        foreach (var line in Lines(bytes))
        {
            ct.ThrowIfCancellationRequested();
            var (id, venue) = ReadVenue(line);
            if (!result.TryAdd(id, venue))
            {
                throw new InvalidDataException("Venue evidence IDs must be unique.");
            }
        }
        return result;
    }

    private static (string Id, VenueMatchRecord Venue) ReadVenue(string line)
    {
        try
        {
            using var document = Parse(Utf8.GetBytes(line));
            var row = document.RootElement;
            if (
                row.GetProperty("artist_partition").GetString() != "development"
                || row.GetProperty("family_partition").GetString() != "development"
                || row.GetProperty("resource").GetProperty("resource_type").GetString() != "venue"
            )
            {
                throw new InvalidDataException("Only development venue evidence is permitted.");
            }
            var id = RequiredText(row, "evidence_id");
            var names = new List<MatchingVenueName>();
            foreach (var field in row.GetProperty("fields").EnumerateArray())
            {
                if (field.GetProperty("path").GetString() != "/raw_record/row/name")
                {
                    continue;
                }
                var name = field.GetProperty("text").GetString();
                if (name == null)
                {
                    continue;
                }
                var span = field.GetProperty("span");
                var length = name.EnumerateRunes().Count();
                if (
                    names.Count != 0
                    || length > 4096
                    || Hash(Utf8.GetBytes(name)) != field.GetProperty("text_sha256").GetString()
                    || span.GetProperty("start").GetInt32() != 0
                    || span.GetProperty("end").GetInt32() != length
                    || span.GetProperty("unit").GetString() != "unicode_code_points"
                    || span.GetProperty("quote").GetString() != name
                )
                {
                    throw new InvalidDataException(
                        "Retained venue name citation is invalid or duplicated."
                    );
                }
                names.Add(new(name));
            }
            return (id, new(null, "", null, names, [], null, LocationAssertions: []));
        }
        catch (Exception exception) when (InvalidJsonShape(exception))
        {
            throw new InvalidDataException(
                "Venue evidence has missing or invalid required fields.",
                exception
            );
        }
    }

    private static async Task<int> WriteComparisons(
        byte[] pairs,
        IReadOnlyDictionary<string, VenueMatchRecord> venues,
        string path,
        CancellationToken ct
    )
    {
        var matcher = new IdentityCandidateMatcher();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        await using var writer = new StreamWriter(
            new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read),
            Utf8
        );
        var count = 0;
        foreach (var line in Lines(pairs))
        {
            ct.ThrowIfCancellationRequested();
            var (taskId, ids) = ReadTask(line);
            if (
                !seen.Add(taskId)
                || ids.Length != 2
                || ids[0] == ids[1]
                || ids.Any(id => id == null || !venues.ContainsKey(id))
            )
            {
                throw new InvalidDataException(
                    "Venue task has duplicate or missing evidence correspondence."
                );
            }
            var comparison = matcher.CompareVenues(venues[ids[0]!], venues[ids[1]!]);
            var output = new
            {
                task_id = taskId,
                evidence_ids = ids,
                comparison,
            };
            await writer.WriteLineAsync(JsonSerializer.Serialize(output, Json).AsMemory(), ct);
            count++;
        }
        await writer.FlushAsync(ct);
        return count;
    }

    private static (string TaskId, string?[] EvidenceIds) ReadTask(string line)
    {
        try
        {
            using var document = Parse(Utf8.GetBytes(line));
            var row = document.RootElement;
            // Deliberately do not deserialize answers, candidate reasons, features or official sources.
            if (
                row.GetProperty("family_partition").GetString() != "development"
                || row.GetProperty("domain").GetString() != "venues"
                || row.GetProperty("task_kind").GetString() != "entity_link"
            )
            {
                throw new InvalidDataException("Only development venue-link tasks are permitted.");
            }
            var taskId = RequiredText(row, "task_id");
            var ids = row.GetProperty("evidence_ids")
                .EnumerateArray()
                .Select(value => value.GetString())
                .ToArray();
            return (taskId, ids);
        }
        catch (Exception exception) when (InvalidJsonShape(exception))
        {
            throw new InvalidDataException(
                "Venue task has missing or invalid required fields.",
                exception
            );
        }
    }

    private static IReadOnlyDictionary<string, VenuePairBaselineFile> ReadManifest(byte[] bytes)
    {
        try
        {
            using var document = Parse(bytes);
            var manifest = document.RootElement;
            if (manifest.GetProperty("schema_version").GetString() != "venue-development-pair-v1")
            {
                throw new InvalidDataException("Unsupported venue development bundle schema.");
            }
            var result = new Dictionary<string, VenuePairBaselineFile>();
            foreach (var (name, _, _) in BundleFiles)
            {
                var file = manifest.GetProperty("files").GetProperty(name);
                result.Add(
                    name,
                    new(
                        RequiredText(file, "sha256"),
                        file.GetProperty("bytes").GetInt64(),
                        file.GetProperty("rows").GetInt32()
                    )
                );
            }
            return result;
        }
        catch (Exception exception) when (InvalidJsonShape(exception))
        {
            throw new InvalidDataException(
                "Venue manifest has missing or invalid required fields.",
                exception
            );
        }
    }

    // These exceptions describe malformed JSON values at the three input projection boundaries.
    // Matcher execution, serialization and output publication are deliberately outside the guards.
    private static bool InvalidJsonShape(Exception exception) =>
        exception
            is KeyNotFoundException
                or InvalidOperationException
                or FormatException
                or OverflowException;

    private static string RequiredText(JsonElement value, string property) =>
        value.GetProperty(property).GetString() is { Length: > 0 } text
            ? text
            : throw new InvalidDataException("Venue bundle identity is missing.");

    private static IEnumerable<string> Lines(byte[] bytes)
    {
        using var reader = new StringReader(Utf8.GetString(bytes));
        while (reader.ReadLine() is { } line)
        {
            if (line.Length > MaximumLineCharacters)
            {
                throw new InvalidDataException("Venue bundle row exceeds its bound.");
            }
            if (!string.IsNullOrWhiteSpace(line))
            {
                yield return line;
            }
        }
    }

    private static async Task<byte[]> ReadBounded(
        string path,
        int maximumBytes,
        CancellationToken ct
    )
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read
        );
        using var bytes = new MemoryStream();
        var buffer = new byte[65536];
        int count;
        while ((count = await stream.ReadAsync(buffer, ct)) != 0)
        {
            if (bytes.Length + count > maximumBytes)
            {
                throw new InvalidDataException("Venue bundle file exceeds its byte bound.");
            }
            bytes.Write(buffer, 0, count);
        }
        return bytes.ToArray();
    }

    private static JsonDocument Parse(byte[] bytes)
    {
        try
        {
            return JsonDocument.Parse(bytes);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Venue bundle contains invalid JSON.", exception);
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static async Task<string> HashFile(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
    }
}

/// <summary>Exact files and code used for the experimental comparison; no label decisions or accuracy estimates.</summary>
public sealed record VenuePairBaselineReport(
    string State,
    string Version,
    string ProjectionVersion,
    string BundleManifestSha256,
    IReadOnlyDictionary<string, VenuePairBaselineFile> InputFiles,
    string MatcherAssemblySha256,
    string RunnerAssemblySha256,
    string OutputFile,
    string OutputSha256,
    int Pairs,
    IReadOnlyList<string> Limits
);

public sealed record VenuePairBaselineFile(string Sha256, long Bytes, int Rows);
