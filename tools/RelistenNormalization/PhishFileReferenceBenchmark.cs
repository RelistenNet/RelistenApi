using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization.Batch;

/// <summary>Scores retained Phish track memberships against independent title-only predictions.</summary>
public sealed record PhishFileReferenceBenchmarkRequest(
    string RetainedAdmissionDescriptorPath,
    string DictionaryShardPath,
    int MaximumSeconds = 300,
    long MaximumOutputBytes = 64 * 1024 * 1024
);

/// <summary>
/// The complete prediction input. Provider song IDs, expected memberships, show dates, file IDs
/// and recording notes are deliberately absent; they remain in the scorer's case envelope.
/// </summary>
public sealed record PhishFileTitleInput(long ArtistId, Guid ArtistUuid, string? Title);

public sealed record PhishFileTitlePrediction(
    string Outcome,
    TrackTitleExtraction? Extraction,
    IReadOnlyList<ShadowRepertoireSegment> Segments,
    IReadOnlyList<string> PredictedSongKeys
);

/// <summary>Per-file set agreement. Repeated title segments do not create extra song memberships.</summary>
public sealed record PhishFileReferenceScore(
    IReadOnlyList<string> Expected,
    IReadOnlyList<string> Predicted,
    IReadOnlyList<string> Intersection,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> Extra
)
{
    public bool Exact => Missing.Count == 0 && Extra.Count == 0;
}

/// <summary>
/// Reuses the production title extractor and shared shadow repertoire resolver. Native song IDs
/// are read only after prediction, by a separate set scorer. This is provider metadata agreement,
/// not verification of audio contents, ordered occurrences, or accepted song identities.
/// </summary>
public sealed class PhishFileReferenceBenchmark
{
    public const string Version = "phish-file-reference-benchmark-v1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };
    private readonly TrackTitleExtractor extractor = new();

    /// <summary>Predicts from the literal title and the separately supplied artist dictionary.</summary>
    public PhishFileTitlePrediction Predict(
        PhishFileTitleInput input,
        ShadowRepertoireAdapter dictionary
    )
    {
        if (input.Title == null)
        {
            return new("unresolved", null, [], []);
        }
        var extraction = extractor.Extract(input.ArtistUuid, input.Title);
        var segments = extraction
            .Segments.Select(segment =>
                dictionary.ResolveTitleMention(
                    input.ArtistId,
                    input.ArtistUuid,
                    segment,
                    extraction.Segments.Count == 1 ? input.Title : null
                )
            )
            .ToArray();
        var outcome =
            segments.Length == 0
            || !extraction.IsResolved
            || segments.Any(segment => segment.Outcome == "unresolved")
                ? "unresolved"
            : segments.All(segment => segment.Outcome == "not_applicable") ? "not_applicable"
            : "proposed";
        return new(
            outcome,
            extraction,
            segments,
            segments
                .Where(segment => segment.Song != null)
                .Select(segment => segment.Song!.EntityKey)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray()
        );
    }

    /// <summary>Compares actual proposals only; competing dictionary candidates are not predictions.</summary>
    public static PhishFileReferenceScore Score(
        PhishFileTitlePrediction prediction,
        IEnumerable<string> expectedSongKeys
    )
    {
        var expected = expectedSongKeys.ToHashSet(StringComparer.Ordinal);
        var predicted = prediction.PredictedSongKeys.ToHashSet(StringComparer.Ordinal);
        return new(
            expected.Order(StringComparer.Ordinal).ToArray(),
            predicted.Order(StringComparer.Ordinal).ToArray(),
            expected.Intersect(predicted).Order(StringComparer.Ordinal).ToArray(),
            expected.Except(predicted).Order(StringComparer.Ordinal).ToArray(),
            predicted.Except(expected).Order(StringComparer.Ordinal).ToArray()
        );
    }

    /// <summary>
    /// Writes a new private report from immutable admissions and original dictionary rows.
    /// Failure retains partial files without a completion manifest. Existing destinations are refused.
    /// </summary>
    public async Task<string> RunAsync(
        PhishFileReferenceBenchmarkRequest request,
        string outputDirectory,
        CancellationToken token = default
    )
    {
        if (Directory.Exists(outputDirectory) || File.Exists(outputDirectory))
        {
            throw new ArgumentException("The benchmark requires a new output directory.");
        }
        if (
            request.MaximumSeconds is <= 0 or > 3600
            || request.MaximumOutputBytes is <= 0 or > 256 * 1024 * 1024
        )
        {
            throw new ArgumentException(
                "Benchmark time and output limits must be positive and bounded."
            );
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(request.MaximumSeconds));
        token = deadline.Token;
        var elapsed = Stopwatch.StartNew();
        var panel = await ReadPanelAsync(request, token);
        var seenFiles = new HashSet<string>(StringComparer.Ordinal);
        var totals = new Totals();
        Directory.CreateDirectory(outputDirectory);
        var filesPath = Path.Combine(outputDirectory, "files.jsonl");
        long outputBytes = 0;

        await using (var output = new FileStream(filesPath, FileMode.CreateNew, FileAccess.Write))
        {
            foreach (var row in panel.Rows)
            {
                var response = await ReadResponseAsync(row, panel, token);
                totals.Responses++;
                foreach (var file in response.Recording.Recording.Files)
                {
                    token.ThrowIfCancellationRequested();
                    var result = EvaluateFile(panel, response, file);
                    if (!seenFiles.Add(result.FileKey))
                    {
                        throw new InvalidDataException(
                            "A provider file occurs more than once in the admitted panel."
                        );
                    }
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(result.Row, Json);
                    if (outputBytes + bytes.LongLength + 1 > request.MaximumOutputBytes)
                    {
                        throw new InvalidDataException(
                            "Benchmark output limit exceeded; no completion manifest was published."
                        );
                    }
                    await output.WriteAsync(bytes, token);
                    await output.WriteAsync("\n"u8.ToArray(), token);
                    outputBytes += bytes.LongLength + 1;
                    totals.Add(result.Score, result.Prediction, result.UnmappedProviderSongIds);
                }
            }
            await output.FlushAsync(token);
        }

        // Close the writer before reopening the completed artifact for its fingerprint.
        token.ThrowIfCancellationRequested();
        return await PublishManifestAsync(
            request,
            outputDirectory,
            panel,
            totals,
            outputBytes,
            elapsed.Elapsed,
            token
        );
    }

    private static async Task<PanelInput> ReadPanelAsync(
        PhishFileReferenceBenchmarkRequest request,
        CancellationToken token
    )
    {
        var descriptorBytes = await File.ReadAllBytesAsync(
            request.RetainedAdmissionDescriptorPath,
            token
        );
        var descriptorArtifact = new RetainedHttpArtifact(
            Path.GetFullPath(request.RetainedAdmissionDescriptorPath),
            descriptorBytes.LongLength,
            Convert.ToHexStringLower(SHA256.HashData(descriptorBytes))
        );
        using var descriptor = JsonDocument.Parse(descriptorBytes);
        if (
            descriptor.RootElement.GetProperty("version").GetString()
                != "retained-http-shadow-admission-v1"
            || descriptor.RootElement.GetProperty("state").GetString() != "completed"
        )
        {
            throw new InvalidDataException(
                "A completed retained HTTP admission descriptor is required."
            );
        }
        var inputsArtifact = descriptor
            .RootElement.GetProperty("inputs")
            .Deserialize<RetainedHttpArtifact>(Json)!;
        var inputBytes = await RetainedHttpProviderInputs.ReadVerifiedAsync(
            inputsArtifact.Path,
            inputsArtifact.Bytes,
            inputsArtifact.Sha256,
            token
        );
        var rows = ReadPhishRows(inputBytes);
        if (rows.Count == 0 || rows.Count > 1000)
        {
            throw new InvalidDataException(
                "Expected between one and 1,000 retained Phish show responses."
            );
        }
        var artist = rows[0].GetProperty("row");
        var artistId = artist.GetProperty("artist_id").GetInt64();
        var artistUuid = artist.GetProperty("artist_uuid").GetGuid();
        var catalog = rows[0]
            .GetProperty("retained_http")
            .Deserialize<RetainedHttpProviderProvenance>(Json)!
            .CatalogBinding.CatalogManifest;
        var dictionary = await ReadDictionaryAsync(
            request.DictionaryShardPath,
            catalog,
            artistId,
            token
        );
        return new(
            rows,
            artistId,
            artistUuid,
            descriptorArtifact,
            inputsArtifact,
            catalog,
            dictionary
        );
    }

    private static async Task<ObservedResponse> ReadResponseAsync(
        JsonElement row,
        PanelInput panel,
        CancellationToken token
    )
    {
        token.ThrowIfCancellationRequested();
        var owner = row.GetProperty("row");
        if (
            owner.GetProperty("artist_id").GetInt64() != panel.ArtistId
            || owner.GetProperty("artist_uuid").GetGuid() != panel.ArtistUuid
        )
        {
            throw new InvalidDataException(
                "Phish admissions disagree on their captured artist identity."
            );
        }
        var provenance = row.GetProperty("retained_http")
            .Deserialize<RetainedHttpProviderProvenance>(Json)!;
        if (provenance.CatalogBinding.CatalogManifest != panel.Catalog)
        {
            throw new InvalidDataException(
                "All cases must use the same captured catalog namespace."
            );
        }
        var originalBytes = await RetainedHttpProviderInputs.ReadVerifiedAsync(
            provenance.Body.Path,
            provenance.Body.Bytes,
            provenance.Body.Sha256,
            token
        );
        var input = row.GetProperty("input").Deserialize<NormalizationInputRevision>(Json)!;
        if (!originalBytes.AsSpan().SequenceEqual(input.Payload))
        {
            throw new InvalidDataException(
                "An admitted Phish response differs from its original HTTP body."
            );
        }
        var source = new ShadowProviderObservations().Read(
            new(row, null, new(panel.ArtistId, panel.ArtistUuid, "Phish", ["phish.in"]), "phish.in")
        );
        if (source.Decoded.Issues.Count > 0 || source.Recordings.Count != 1)
        {
            throw new InvalidDataException(
                "The retained Phish response has incomplete or conflicting file identities."
            );
        }
        var recording = source.Recordings.Single();
        if (
            recording.CatalogSourceUuid == null
            || recording.Recording.Origin.Item.ResourceType != "show"
        )
        {
            throw new InvalidDataException(
                "A retained exact source/provider association is required."
            );
        }
        return new(source, recording, provenance);
    }

    private FileEvaluation EvaluateFile(
        PanelInput panel,
        ObservedResponse response,
        ProviderFileObservation file
    )
    {
        var track = file
            .Identities.Single(identity => identity.Identity.ResourceType == "track")
            .Identity;
        if (file.RecordingIdentity?.Identity != response.Recording.Recording.Origin.Item)
        {
            throw new InvalidDataException("A provider file must identify its enclosing show.");
        }
        var fileKey =
            $"phish.in/show/{response.Source.Input.Resource.ProviderKey}/track/{track.ProviderKey}";

        // Only this DTO crosses the prediction boundary. The retained file and its
        // expected song IDs are deliberately kept in the scorer's surrounding scope.
        var predictionInput = new PhishFileTitleInput(
            panel.ArtistId,
            panel.ArtistUuid,
            file.Title?.Value
        );
        var prediction = Predict(predictionInput, panel.Dictionary.Resolver);
        var providerKeys = panel.Dictionary.ProviderSongs;
        var expected = file
            .SongIdentities.Select(identity => identity.Identity.ProviderKey)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var unmapped = expected.Where(key => !providerKeys.ContainsKey(key)).ToArray();
        var score = Score(
            prediction,
            expected.Select(key =>
                providerKeys.TryGetValue(key, out var song)
                    ? ShadowRepertoireAdapter.KnownEntityKey(song)
                    : "unmapped:phish.in/song/" + key
            )
        );
        var row = new
        {
            fileKey,
            artistUuid = panel.ArtistUuid,
            sourceUuid = response.Recording.CatalogSourceUuid,
            showUuid = response.Recording.CatalogShowUuid,
            response.Source.Input.Resource,
            providerTrack = track,
            file.Origin,
            titleEvidence = file.Title?.Evidence,
            referenceEvidence = file.SongIdentities,
            originalResponse = response.Provenance.Body,
            predictionInput,
            prediction,
            expectedProviderSongIds = expected,
            unmappedProviderSongIds = unmapped,
            score,
        };
        return new(fileKey, row, score, prediction, unmapped.Length);
    }

    private static async Task<string> PublishManifestAsync(
        PhishFileReferenceBenchmarkRequest request,
        string outputDirectory,
        PanelInput panel,
        Totals totals,
        long outputBytes,
        TimeSpan elapsed,
        CancellationToken token
    )
    {
        var manifest = new
        {
            version = Version,
            state = "completed",
            method = TrackTitleExtractor.Version + "/" + panel.Dictionary.Resolver.Version,
            elapsedSeconds = elapsed.TotalSeconds,
            predictionFields = new[] { "artistId", "artistUuid", "title" },
            qualification = "Development-exposed provider metadata agreement. Expected file song IDs never enter prediction. This does not establish audio contents, order, repeats, or accepted identity.",
            inputs = new
            {
                descriptor = panel.Descriptor,
                admissions = panel.Admissions,
                dictionary = panel.Dictionary.Artifact,
                catalog = panel.Catalog,
            },
            totals,
            precision = totals.Predicted == 0
                ? (double?)null
                : (double)totals.Intersection / totals.Predicted,
            recall = totals.Expected == 0
                ? (double?)null
                : (double)totals.Intersection / totals.Expected,
            files = await RetainedHttpProviderInputs.FingerprintAsync(
                Path.Combine(outputDirectory, "files.jsonl"),
                token
            ),
        };
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, Json);
        if (outputBytes + manifestBytes.LongLength > request.MaximumOutputBytes)
        {
            throw new InvalidDataException("Benchmark manifest exceeds the output limit.");
        }
        var pending = Path.Combine(outputDirectory, "manifest.partial.json");
        var complete = Path.Combine(outputDirectory, "manifest.json");
        await File.WriteAllBytesAsync(pending, manifestBytes, token);
        token.ThrowIfCancellationRequested();
        File.Move(pending, complete);
        return complete;
    }

    private static List<JsonElement> ReadPhishRows(byte[] inputBytes)
    {
        var rows = new List<JsonElement>();
        using var reader = new StringReader(Encoding.UTF8.GetString(inputBytes));
        while (reader.ReadLine() is { } line)
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.GetProperty("provider").GetString() == "phish.in")
            {
                rows.Add(document.RootElement.Clone());
            }
        }
        return rows;
    }

    private static async Task<DictionaryInput> ReadDictionaryAsync(
        string path,
        RetainedHttpArtifact catalog,
        long artistId,
        CancellationToken token
    )
    {
        var catalogBytes = await RetainedHttpProviderInputs.ReadVerifiedAsync(
            catalog.Path,
            catalog.Bytes,
            catalog.Sha256,
            token
        );
        using var manifest = JsonDocument.Parse(catalogBytes);
        if (!manifest.RootElement.GetProperty("complete").GetBoolean())
        {
            throw new InvalidDataException("The original catalog capture is incomplete.");
        }
        var directory = Path.GetDirectoryName(Path.GetFullPath(catalog.Path))!;
        var shard = manifest
            .RootElement.GetProperty("completedShards")
            .EnumerateArray()
            .Single(value =>
                value
                    .GetProperty("shardId")
                    .GetString()!
                    .Contains("-songs-", StringComparison.Ordinal)
                && Path.GetFullPath(
                    Path.Combine(directory, value.GetProperty("gzipPath").GetString()!)
                ) == Path.GetFullPath(path)
            );
        var compressed = shard.GetProperty("compressed");
        var bytes = await RetainedHttpProviderInputs.ReadVerifiedAsync(
            path,
            compressed.GetProperty("bytes").GetInt64(),
            compressed.GetProperty("sha256").GetString()!,
            token
        );
        await using var gzip = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        var resolver = new ShadowRepertoireAdapter();
        var songs = new Dictionary<string, Guid>(StringComparer.Ordinal);
        long count = 0;
        var complete = false;
        while (await reader.ReadLineAsync(token) is { } line)
        {
            using var document = JsonDocument.Parse(line);
            var record = document.RootElement;
            var kind = record.GetProperty("kind").GetString();
            if (kind == "song_sample")
            {
                if (complete)
                {
                    throw new InvalidDataException(
                        "The original dictionary contains rows after its footer."
                    );
                }
                count++;
                var row = record.GetProperty("row");
                if (row.GetProperty("artist_id").GetInt64() != artistId)
                {
                    continue;
                }
                var key = row.GetProperty("upstream_identifier").GetString()!;
                if (!songs.TryAdd(key, row.GetProperty("uuid").GetGuid()))
                {
                    throw new InvalidDataException(
                        "A provider song ID has several captured dictionary identities."
                    );
                }
                resolver.Observe(record);
            }
            else if (kind == "snapshot_complete")
            {
                complete = record.GetProperty("shard_rows").GetInt64() == count;
            }
        }
        if (!complete || count != shard.GetProperty("actualRows").GetInt64() || songs.Count == 0)
        {
            throw new InvalidDataException(
                "The original dictionary shard is incomplete or has no Phish repertoire."
            );
        }
        return new(
            resolver,
            songs,
            new RetainedHttpArtifact(
                Path.GetFullPath(path),
                bytes.LongLength,
                compressed.GetProperty("sha256").GetString()!
            )
        );
    }

    private sealed record DictionaryInput(
        ShadowRepertoireAdapter Resolver,
        Dictionary<string, Guid> ProviderSongs,
        RetainedHttpArtifact Artifact
    );

    private sealed record PanelInput(
        IReadOnlyList<JsonElement> Rows,
        long ArtistId,
        Guid ArtistUuid,
        RetainedHttpArtifact Descriptor,
        RetainedHttpArtifact Admissions,
        RetainedHttpArtifact Catalog,
        DictionaryInput Dictionary
    );

    private sealed record ObservedResponse(
        ShadowProviderObservationSet Source,
        ShadowProviderRecordingObservation Recording,
        RetainedHttpProviderProvenance Provenance
    );

    private sealed record FileEvaluation(
        string FileKey,
        object Row,
        PhishFileReferenceScore Score,
        PhishFileTitlePrediction Prediction,
        int UnmappedProviderSongIds
    );

    private sealed class Totals
    {
        public int Responses { get; set; }
        public int Files { get; private set; }
        public int MultiSongFiles { get; private set; }
        public int ExactFiles { get; private set; }
        public int UnresolvedFiles { get; private set; }
        public int Expected { get; private set; }
        public int Predicted { get; private set; }
        public int Intersection { get; private set; }
        public int Missing { get; private set; }
        public int Extra { get; private set; }
        public int UnmappedProviderSongIds { get; private set; }

        public void Add(
            PhishFileReferenceScore score,
            PhishFileTitlePrediction prediction,
            int unmapped
        )
        {
            Files++;
            MultiSongFiles += score.Expected.Count > 1 ? 1 : 0;
            ExactFiles += score.Exact ? 1 : 0;
            UnresolvedFiles += prediction.Outcome == "unresolved" ? 1 : 0;
            Expected += score.Expected.Count;
            Predicted += score.Predicted.Count;
            Intersection += score.Intersection.Count;
            Missing += score.Missing.Count;
            Extra += score.Extra.Count;
            UnmappedProviderSongIds += unmapped;
        }
    }
}
