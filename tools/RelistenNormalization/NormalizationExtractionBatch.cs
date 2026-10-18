using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Relisten.Normalization.Batch;

/// <summary>
/// Runs local extractors over a retained read-only scale capture. Results are metadata
/// claims for inspection, never accepted people, repertoire or composition identities.
/// No host, database, provider client or paid model is started by this runner.
/// </summary>
public sealed class NormalizationExtractionBatch(bool includeSourceActorAliases = false)
{
    public const string Version = "normalization-extraction-batch-v1";
    public const string SourceActorVersion = "normalization-source-actor-extraction-batch-v1";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    private static readonly HashSet<string> ContextKinds =
    [
        "schema",
        "indexes",
        "source_profile",
        "track_profile",
        "venue_profile",
        "song_profile",
        "setlist_profile",
    ];

    /// <summary>
    /// Streams a capture into a new directory. A completed snapshot publishes a manifest
    /// with state completed or completed_partial; item failures keep their input and an
    /// error record. Missing completion markers and cancellation retain partial files
    /// without a completed manifest. Existing output directories are never reused.
    /// </summary>
    public async Task<NormalizationExtractionBatchReport> RunAsync(
        string inputPath,
        string outputDirectory,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        inputPath = Path.GetFullPath(inputPath);
        outputDirectory = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(outputDirectory) || File.Exists(outputDirectory))
        {
            throw new IOException(
                "The extraction output path already exists. Choose a new directory."
            );
        }

        await using var input = new FileStream(
            inputPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan
        );
        Directory.CreateDirectory(outputDirectory);
        var partialPath = Path.Combine(outputDirectory, "extractions.partial.jsonl");
        await using var output = new StreamWriter(
            new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(false),
            65536
        );
        using var hash = SHA256.Create();
        await using var hashingInput = new CryptoStream(
            input,
            hash,
            CryptoStreamMode.Read,
            leaveOpen: true
        );
        using var reader = new StreamReader(
            hashingInput,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 65536,
            leaveOpen: true
        );
        var run = new ExtractionRun(output, includeSourceActorAliases);
        var reachedEnd = false;
        var cancelled = false;

        try
        {
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await run.ProcessLineAsync(line);
            }

            reachedEnd = true;
            run.ValidateCompletion();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancelled = true;
        }

        await output.FlushAsync(CancellationToken.None);
        var report = run.Report(
            inputPath,
            reachedEnd ? Convert.ToHexStringLower(hash.Hash!) : null,
            cancelled
        );
        // Close the writer before renaming, so the published JSONL is complete on all platforms.
        output.Close();
        if (report.State is "completed" or "completed_partial")
        {
            File.Move(partialPath, Path.Combine(outputDirectory, "extractions.jsonl"));
            await WriteReportAsync(Path.Combine(outputDirectory, "manifest.json"), report);
        }
        else
        {
            await WriteReportAsync(Path.Combine(outputDirectory, "report.partial.json"), report);
        }

        return report;
    }

    private static async Task WriteReportAsync(
        string path,
        NormalizationExtractionBatchReport report
    )
    {
        var partialPath = path + ".writing";
        await using (
            var stream = new FileStream(
                partialPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read
            )
        )
        {
            await JsonSerializer.SerializeAsync(stream, report, Json);
        }

        File.Move(partialPath, path);
    }

    /// <summary>
    /// Holds counters and artist scope for one run. Only artist metadata and counters
    /// remain in memory; retained source prose and file titles stream to JSONL.
    /// </summary>
    private sealed class ExtractionRun(StreamWriter output, bool includeSourceActorAliases)
    {
        private readonly CatalogRowExtraction extractor = new(includeSourceActorAliases);
        private readonly Dictionary<long, Guid> artists = new();
        private readonly HashSet<(string Kind, Guid Uuid)> seenResources = new();
        private readonly Dictionary<string, long> kinds = new();
        private readonly Dictionary<string, long> unsupported = new();
        private readonly Dictionary<string, long> roles = new();
        private readonly Dictionary<long, NormalizationArtistExtractionCounts> artistCounts = new();
        private readonly Dictionary<string, long> errorReasons = new();
        private readonly Dictionary<string, long> missingTrackContext = new();
        private JsonElement? snapshot;
        private JsonElement? completion;
        private long? expectedArtists;
        private long lineNumber;
        private long outputRecords;
        private long extractedRecords;
        private long errorCount;
        private bool invalidSnapshot;

        internal async Task ProcessLineAsync(string line)
        {
            lineNumber++;
            JsonElement record;
            try
            {
                using var document = JsonDocument.Parse(line);
                record = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                if (completion is not null)
                {
                    invalidSnapshot = true;
                }

                await WriteErrorAsync("invalid_json", null, line);
                return;
            }

            try
            {
                var kind = record.GetProperty("kind").GetString()!;
                Count(kinds, kind);
                if (lineNumber == 1 && kind != "snapshot")
                {
                    invalidSnapshot = true;
                    await WriteErrorAsync("missing_snapshot_begin", record);
                }

                if (completion is not null)
                {
                    invalidSnapshot = true;
                    await WriteErrorAsync("record_after_snapshot_complete", record);
                    return;
                }

                switch (kind)
                {
                    case "snapshot":
                        ReadSnapshot(record);
                        break;
                    case "snapshot_complete":
                        record.GetProperty("completed_at").GetString();
                        completion = record;
                        break;
                    case "artist":
                        var artist = record.GetProperty("row");
                        artists.Add(
                            artist.GetProperty("id").GetInt64(),
                            artist.GetProperty("uuid").GetGuid()
                        );
                        break;
                    case "totals":
                        expectedArtists = record
                            .GetProperty("rows")
                            .GetProperty("artists")
                            .GetInt64();
                        break;
                    case "source_sample":
                    case "track_sample":
                        await ExtractAsync(kind, record);
                        break;
                    default:
                        if (!ContextKinds.Contains(kind))
                        {
                            Count(unsupported, kind);
                        }
                        break;
                }
            }
            catch (Exception exception)
                when (exception
                        is JsonException
                            or KeyNotFoundException
                            or FormatException
                            or ArgumentException
                            or InvalidOperationException
                            or RegexMatchTimeoutException
                )
            {
                await WriteErrorAsync(
                    exception is RegexMatchTimeoutException
                        ? "extractor_timeout"
                        : "invalid_record",
                    record
                );
            }
        }

        private void ReadSnapshot(JsonElement record)
        {
            if (
                lineNumber != 1
                || snapshot is not null
                || record.GetProperty("read_only").GetString() != "on"
                || record.GetProperty("in_recovery").GetBoolean() != true
                || string.IsNullOrEmpty(record.GetProperty("snapshot").GetString())
            )
            {
                invalidSnapshot = true;
                throw new FormatException(
                    "Expected one read-only replica snapshot at the beginning."
                );
            }

            snapshot = record;
        }

        private async Task ExtractAsync(string kind, JsonElement record)
        {
            var row = record.GetProperty("row");
            var artistId = row.GetProperty("artist_id").GetInt64();
            var artistUuid = artists[artistId];
            var resourceUuid = row.GetProperty("uuid").GetGuid();
            if (!seenResources.Add((kind, resourceUuid)))
            {
                throw new FormatException("A resource was sampled more than once.");
            }

            var result = extractor.Extract(record, artistUuid);
            var creditExtraction = result.CreditExtraction;
            var titleExtraction = result.TitleExtraction;
            if (result.TrackContext is { } context)
            {
                CountMissingTrackContext(context);
            }
            await output.WriteLineAsync(JsonSerializer.Serialize(result, Json));
            outputRecords++;
            extractedRecords++;
            artistCounts.TryGetValue(artistId, out var counts);
            counts ??= new(artistUuid, 0, 0, 0, 0, 0);
            artistCounts[artistId] = counts with
            {
                Sources = counts.Sources + (creditExtraction is null ? 0 : 1),
                Tracks = counts.Tracks + (titleExtraction is null ? 0 : 1),
                CreditMentions = counts.CreditMentions + (creditExtraction?.Credits.Count ?? 0),
                TitleSegments = counts.TitleSegments + (titleExtraction?.Segments.Count ?? 0),
                UnresolvedTitles =
                    counts.UnresolvedTitles + (titleExtraction is { IsResolved: false } ? 1 : 0),
            };

            foreach (var credit in creditExtraction?.Credits ?? [])
            {
                Count(roles, JsonNamingPolicy.SnakeCaseLower.ConvertName(credit.Role.ToString()));
            }
        }

        private void CountMissingTrackContext(NormalizationTrackInputContext context)
        {
            if (context.SourceUuid is null)
            {
                Count(missingTrackContext, "source_context");
            }
            if (context.SourceSetUuid is null)
            {
                Count(missingTrackContext, "set_context");
            }
            if (context.ShowUuid is null)
            {
                Count(missingTrackContext, "show_context");
            }
        }

        internal void ValidateCompletion()
        {
            if (snapshot is null || completion is null || expectedArtists != artists.Count)
            {
                invalidSnapshot = true;
                RecordError("incomplete_snapshot");
            }
        }

        private async Task WriteErrorAsync(
            string reason,
            JsonElement? input,
            string? rawLine = null
        )
        {
            RecordError(reason);
            await output.WriteLineAsync(
                JsonSerializer.Serialize(
                    new
                    {
                        kind = "extraction_error",
                        lineNumber,
                        reason,
                        input,
                        rawLine,
                    },
                    Json
                )
            );
            outputRecords++;
        }

        private void RecordError(string reason)
        {
            errorCount++;
            Count(errorReasons, reason);
        }

        internal NormalizationExtractionBatchReport Report(
            string inputPath,
            string? inputSha256,
            bool cancelled
        ) =>
            new(
                cancelled ? "cancelled"
                    : invalidSnapshot ? "incomplete"
                    : errorCount > 0 ? "completed_partial"
                    : "completed",
                inputPath,
                inputSha256,
                inputSha256 is null ? null : RunIdentity(inputSha256),
                snapshot,
                completion,
                lineNumber,
                outputRecords,
                extractedRecords,
                errorCount,
                ExtractorVersions(),
                kinds,
                unsupported,
                roles,
                missingTrackContext,
                artistCounts,
                errorReasons
            );

        private IReadOnlyDictionary<string, string> ExtractorVersions()
        {
            var versions = new Dictionary<string, string>
            {
                ["recording_credits"] = RecordingCreditExtractor.Version,
                ["track_title"] = TrackTitleExtractor.Version,
            };
            if (includeSourceActorAliases)
            {
                versions.Add("source_actor_aliases", SourceActorAliasExtractor.Version);
            }

            return versions;
        }

        private string RunIdentity(string inputSha256) =>
            Convert.ToHexStringLower(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(
                        includeSourceActorAliases
                            ? $"{SourceActorVersion}\n{inputSha256}\nrecording_credits={RecordingCreditExtractor.Version}\ntrack_title={TrackTitleExtractor.Version}\nsource_actor_aliases={SourceActorAliasExtractor.Version}\n"
                            : $"{Version}\n{inputSha256}\nrecording_credits={RecordingCreditExtractor.Version}\ntrack_title={TrackTitleExtractor.Version}\n"
                    )
                )
            );

        private static void Count(Dictionary<string, long> counts, string key)
        {
            counts.TryGetValue(key, out var count);
            counts[key] = count + 1;
        }
    }
}
