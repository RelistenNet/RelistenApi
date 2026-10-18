using System.Text.Json;

namespace Relisten.Normalization.Batch;

/// <summary>
/// Streams the retained catalog and joins the existing extraction stream in its
/// original order. Configured provider mappings remain context, not recording ownership.
/// </summary>
internal sealed class ShadowCatalogInputs(string snapshotPath, string? providerInputsPath)
{
    internal Dictionary<long, ShadowCatalogArtist> Artists { get; } = new();
    internal Dictionary<string, long> InputKinds { get; } = new();
    internal Dictionary<string, long> ConfiguredProviders { get; } = new();
    internal Dictionary<string, string> PreparationFailures { get; } = new();
    internal Dictionary<string, long> ExpectedOutcomes { get; } = new();
    internal JsonElement? Population { get; private set; }

    internal async Task ObserveAsync(
        IReadOnlyList<IShadowCatalogAdapter> adapters,
        string extractionsPath,
        CancellationToken ct
    )
    {
        await foreach (var record in ReadRecordsAsync(ct))
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
                foreach (var domain in adapter.DomainsFor(kind))
                {
                    var key = Provider(record) + "|" + domain;
                    ExpectedOutcomes[key] = ExpectedOutcomes.GetValueOrDefault(key) + 1;
                }

                ObserveAdapter(adapter, record);
            }
        }

        await foreach (var extraction in ReadJsonLinesAsync(extractionsPath, ct))
        {
            foreach (var adapter in adapters)
            {
                ObserveAdapter(adapter, extraction);
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
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct
    )
    {
        await foreach (var record in ReadJsonLinesAsync(snapshotPath, ct))
        {
            yield return record;
        }

        if (providerInputsPath != null)
        {
            await foreach (var record in ReadJsonLinesAsync(providerInputsPath, ct))
            {
                yield return record;
            }
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
        Artists[row.GetProperty("id").GetInt64()] = new(
            row.GetProperty("id").GetInt64(),
            row.GetProperty("uuid").GetGuid(),
            row.GetProperty("name").GetString()!,
            providers
        );
        foreach (var provider in providers)
        {
            ConfiguredProviders[provider] = ConfiguredProviders.GetValueOrDefault(provider) + 1;
        }
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
}
