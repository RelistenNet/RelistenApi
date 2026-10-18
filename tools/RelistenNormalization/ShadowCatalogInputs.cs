using System.Text.Json;

namespace Relisten.Normalization.Batch;

/// <summary>
/// Streams the retained catalog and joins the existing extraction stream in its
/// original order. Configured provider mappings remain context, not recording ownership.
/// </summary>
internal sealed class ShadowCatalogInputs
{
    private readonly string[] snapshotPaths;
    private readonly string? providerInputsPath;
    private readonly ShadowCatalogCapture? capture;
    private readonly CatalogRowExtraction inlineExtractor = new();
    private readonly Dictionary<Guid, long> artistIds = [];

    internal ShadowCatalogInputs(string snapshotPath, string? providerInputsPath)
    {
        snapshotPaths = [snapshotPath];
        this.providerInputsPath = providerInputsPath;
    }

    internal ShadowCatalogInputs(ShadowCatalogCapture capture)
    {
        this.capture = capture;
        snapshotPaths = capture.Descriptor.Files.Select(file => file.Path).ToArray();
        providerInputsPath = capture.Descriptor.ProviderInput?.Path;
    }

    internal Dictionary<long, ShadowCatalogArtist> Artists { get; } = new();
    internal Dictionary<string, long> InputKinds { get; } = new();
    internal Dictionary<string, long> ConfiguredProviders { get; } = new();
    internal Dictionary<string, string> PreparationFailures { get; } = new();
    internal Dictionary<string, long> ExpectedOutcomes { get; } = new();
    internal JsonElement? Population { get; private set; }
    internal List<CapturedFrame> Frames { get; } = [];
    internal long InlineExtractionErrors { get; private set; }
    internal IReadOnlyList<string> SnapshotPaths => snapshotPaths;
    internal string? ProviderInputsPath => providerInputsPath;

    internal async Task ObserveAsync(
        IReadOnlyList<IShadowCatalogAdapter> adapters,
        string? extractionsPath,
        CancellationToken ct,
        bool providerInputsOnly = false
    )
    {
        await foreach (var record in ReadRecordsAsync(ct, retainFrames: true))
        {
            var kind = record.GetProperty("kind").GetString()!;
            InputKinds[kind] = InputKinds.GetValueOrDefault(kind) + 1;
            if (kind == "artist")
            {
                AddArtist(record);
            }
            else if (kind == "totals")
            {
                Population = record.Clone();
            }

            foreach (var adapter in adapters)
            {
                if (!providerInputsOnly || kind == "provider_input")
                {
                    foreach (var domain in adapter.DomainsFor(kind))
                    {
                        var key = Provider(record) + "|" + domain;
                        ExpectedOutcomes[key] = ExpectedOutcomes.GetValueOrDefault(key) + 1;
                    }
                }

                ObserveAdapter(adapter, record);
            }

            if (extractionsPath == null)
            {
                var extracted = ExtractInline(record);
                if (extracted.ExtractionError != null)
                {
                    InlineExtractionErrors++;
                }
                if ((extracted.Extraction ?? extracted.ExtractionError) is { } observation)
                {
                    foreach (var adapter in adapters)
                    {
                        ObserveAdapter(adapter, observation);
                    }
                }
            }
        }

        if (extractionsPath != null)
        {
            await foreach (var extraction in ReadJsonLinesAsync(extractionsPath, ct))
            {
                foreach (var adapter in adapters)
                {
                    ObserveAdapter(adapter, extraction);
                }
            }
        }
    }

    private void ObserveAdapter(IShadowCatalogAdapter adapter, JsonElement record)
    {
        var name = adapter.GetType().Name;
        if (PreparationFailures.ContainsKey(name))
        {
            return;
        }

        try
        {
            adapter.Observe(record);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // A partial retrieval index cannot support that adapter's decisions.
            // Keep other domains running and emit explicit failures for this one.
            PreparationFailures[name] = error.GetType().Name + ": " + error.Message;
        }
    }

    internal async IAsyncEnumerable<JsonElement> ReadRecordsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct,
        bool retainFrames = false
    )
    {
        for (var ordinal = 0; ordinal < snapshotPaths.Length; ordinal++)
        {
            JsonElement? header = null;
            JsonElement? footer = null;
            long payloadRows = 0;
            await foreach (var record in ReadJsonLinesAsync(snapshotPaths[ordinal], ct))
            {
                var kind = record.GetProperty("kind").GetString();
                if (capture != null && (footer != null || (header == null && kind != "snapshot")))
                {
                    throw new InvalidDataException(
                        "A capture shard must retain its original snapshot and final footer."
                    );
                }

                if (kind == "snapshot")
                {
                    if (capture != null && header != null)
                    {
                        throw new InvalidDataException(
                            "A shard cannot contain more than one snapshot header."
                        );
                    }
                    if (
                        capture != null
                        && (
                            record.GetProperty("read_only").GetString() != "on"
                            || !record.GetProperty("in_recovery").GetBoolean()
                            || string.IsNullOrEmpty(record.GetProperty("snapshot").GetString())
                        )
                    )
                    {
                        throw new InvalidDataException(
                            "Expected an original read-only replica snapshot header."
                        );
                    }
                    header = record.Clone();
                }
                else if (kind == "snapshot_complete")
                {
                    footer = record.Clone();
                }
                else if (kind is not ("artist" or "totals"))
                {
                    payloadRows++;
                }

                yield return record;
            }

            if (capture != null)
            {
                var expected = capture.Descriptor.Files[ordinal].ActualRows;
                if (
                    header == null
                    || footer == null
                    || (expected.HasValue && expected.Value != payloadRows)
                    || (
                        footer.Value.TryGetProperty("shard_rows", out var rows)
                        && rows.GetInt64() != payloadRows
                    )
                )
                {
                    throw new InvalidDataException(
                        "Capture shard rows do not reconcile with its retained footer and descriptor."
                    );
                }
            }
            if (retainFrames && header is { } start && footer is { } end)
            {
                Frames.Add(new(snapshotPaths[ordinal], start, end, payloadRows));
            }
        }

        if (providerInputsPath != null)
        {
            await foreach (var record in ReadJsonLinesAsync(providerInputsPath, ct))
            {
                yield return record;
            }
        }
    }

    /// <summary>
    /// External extraction stays in its original lockstep order. Inline extraction
    /// creates one short-lived typed result per row in each pass; no second corpus is
    /// written or retained. An extraction error keeps its original input coordinates.
    /// </summary>
    internal async IAsyncEnumerable<PreparedRow> ReadPreparedAsync(
        string? extractionsPath,
        long completedLine,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct
    )
    {
        await using var extractions =
            extractionsPath == null
                ? null
                : ReadJsonLinesAsync(extractionsPath, ct).GetAsyncEnumerator(ct);
        long line = 0;
        await foreach (var record in ReadRecordsAsync(ct))
        {
            line++;
            var kind = record.GetProperty("kind").GetString();
            if (
                kind is not ("source_sample" or "track_sample")
                || (extractions == null && line <= completedLine)
            )
            {
                yield return new(record, null, null);
                continue;
            }

            if (extractions == null)
            {
                yield return ExtractInline(record);
                continue;
            }

            if (!await extractions.MoveNextAsync())
            {
                throw new InvalidOperationException(
                    "Extraction stream ended before its corresponding catalog row."
                );
            }

            var extracted = extractions.Current;
            var isError = extracted.GetProperty("kind").GetString() == "extraction_error";
            var extractedUuid = isError
                ? extracted.GetProperty("input").GetProperty("row").GetProperty("uuid").GetGuid()
                : extracted.GetProperty("resourceUuid").GetGuid();
            if (extractedUuid != record.GetProperty("row").GetProperty("uuid").GetGuid())
            {
                throw new InvalidOperationException(
                    "Extraction stream no longer matches catalog input order."
                );
            }

            yield return new(record, isError ? null : extracted, isError ? extracted : null);
        }

        if (extractions != null && await extractions.MoveNextAsync())
        {
            throw new InvalidOperationException("Extraction stream has unmatched trailing rows.");
        }
    }

    private PreparedRow ExtractInline(JsonElement record)
    {
        if (record.GetProperty("kind").GetString() is not ("source_sample" or "track_sample"))
        {
            return new(record, null, null);
        }

        try
        {
            var artistId = record.GetProperty("row").GetProperty("artist_id").GetInt64();
            return new(record, inlineExtractor.ExtractJson(record, Artists[artistId].Uuid), null);
        }
        catch (Exception error)
            when (error
                    is JsonException
                        or KeyNotFoundException
                        or FormatException
                        or ArgumentException
                        or InvalidOperationException
                        or System.Text.RegularExpressions.RegexMatchTimeoutException
            )
        {
            return new(
                record,
                null,
                JsonSerializer.SerializeToElement(
                    new
                    {
                        kind = "extraction_error",
                        reason = error.GetType().Name + ": " + error.Message,
                        input = record,
                    }
                )
            );
        }
    }

    internal ShadowCatalogInput Project(
        JsonElement record,
        JsonElement? extraction,
        JsonElement? extractionError = null
    )
    {
        var row = record.GetProperty("row");
        ShadowCatalogArtist? artist = null;
        if (row.TryGetProperty("artist_id", out var id) && id.TryGetInt64(out var artistId))
        {
            Artists.TryGetValue(artistId, out artist);
        }

        if (artist == null && row.TryGetProperty("artist_uuid", out var uuid))
        {
            var artistUuid = uuid.GetGuid();
            artist = Artists.Values.FirstOrDefault(value => value.Uuid == artistUuid);
            artist ??= new(
                0,
                artistUuid,
                row.TryGetProperty("artist_name", out var name)
                    ? name.GetString()!
                    : artistUuid.ToString(),
                []
            );
        }

        artist ??= new(0, Guid.Empty, "Unknown artist", []);
        return new(record, extraction, artist, Provider(record), extractionError);
    }

    private void AddArtist(JsonElement record)
    {
        var row = record.GetProperty("row");
        var providers = record
            .GetProperty("configured_provider_mappings")
            .EnumerateArray()
            .Select(mapping => mapping.GetProperty("provider_name").GetString()!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var id = row.GetProperty("id").GetInt64();
        var uuid = row.GetProperty("uuid").GetGuid();
        Artists.TryGetValue(id, out var previous);
        if (
            (previous != null && previous.Uuid != uuid)
            || (artistIds.TryGetValue(uuid, out var previousId) && previousId != id)
        )
        {
            throw new InvalidDataException("Captured artist IDs and UUIDs conflict across shards.");
        }

        foreach (
            var provider in providers.Except(
                previous?.ConfiguredProviders ?? [],
                StringComparer.Ordinal
            )
        )
        {
            ConfiguredProviders[provider] = ConfiguredProviders.GetValueOrDefault(provider) + 1;
        }
        Artists[id] = new(
            id,
            uuid,
            row.GetProperty("name").GetString()!,
            providers.Union(previous?.ConfiguredProviders ?? [], StringComparer.Ordinal).ToArray()
        );
        artistIds[uuid] = id;
    }

    private static string Provider(JsonElement record)
    {
        if (record.TryGetProperty("provider", out var provider))
        {
            return provider.GetString()!;
        }

        var kind = record.GetProperty("kind").GetString();
        var row = record.GetProperty("row");
        if (
            kind == "track_sample"
            && row.TryGetProperty("mp3_url", out var url)
            && Uri.TryCreate(url.GetString(), UriKind.Absolute, out var parsed)
        )
        {
            return "playback:" + parsed.Host;
        }

        if (
            kind == "setlist_sample"
            && row.TryGetProperty("upstream_identifier", out var identifier)
        )
        {
            var value = identifier.GetString();
            if (value?.StartsWith("pt:", StringComparison.Ordinal) == true)
            {
                return "phantasytour.com";
            }
        }

        // The legacy source/venue/song row has no authoritative provider column.
        // An artist's configured mappings cannot resolve ownership of that row.
        return "catalog:unattributed";
    }

    internal static async IAsyncEnumerable<JsonElement> ReadJsonLinesAsync(
        string path,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct
    )
    {
        await using var file = File.OpenRead(path);
        await using Stream input = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
            ? new System.IO.Compression.GZipStream(
                file,
                System.IO.Compression.CompressionMode.Decompress,
                leaveOpen: true
            )
            : file;
        using var reader = new StreamReader(input);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            ct.ThrowIfCancellationRequested();
            using var document = JsonDocument.Parse(line);
            yield return document.RootElement;
        }
    }

    internal sealed record PreparedRow(
        JsonElement Record,
        JsonElement? Extraction,
        JsonElement? ExtractionError
    );

    internal sealed record CapturedFrame(
        string Path,
        JsonElement Header,
        JsonElement Footer,
        long PayloadRows
    );
}
