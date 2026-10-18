using System.IO.Compression;
using System.Text.Json;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization.Batch;

public sealed partial class RetainedHttpProviderInputs
{
    private static async Task<(CatalogIndex Index, RetainedHttpArtifact Manifest)> ReadCatalogAsync(
        string manifestPath,
        HashSet<Guid> selectedSources,
        CancellationToken ct
    )
    {
        var bytes = await File.ReadAllBytesAsync(manifestPath, ct);
        var artifact = new RetainedHttpArtifact(
            Path.GetFullPath(manifestPath),
            bytes.LongLength,
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))
        );
        using var manifest = JsonDocument.Parse(bytes);
        if (!manifest.RootElement.GetProperty("complete").GetBoolean())
        {
            throw new InvalidDataException("Catalog manifest is incomplete.");
        }
        var result = new CatalogIndex();
        var directory = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        foreach (var shard in manifest.RootElement.GetProperty("completedShards").EnumerateArray())
        {
            if (
                !shard
                    .GetProperty("shardId")
                    .GetString()!
                    .Contains("-sources-", StringComparison.Ordinal)
            )
            {
                continue;
            }
            var path = ContainedPath(directory, shard.GetProperty("gzipPath").GetString()!);
            var declared = shard.GetProperty("compressed");
            var actual = await FingerprintAsync(path, ct);
            if (
                actual.Bytes != declared.GetProperty("bytes").GetInt64()
                || !StringComparer.OrdinalIgnoreCase.Equals(
                    actual.Sha256,
                    declared.GetProperty("sha256").GetString()
                )
            )
            {
                throw new InvalidDataException("Catalog source shard differs from its manifest.");
            }
            await ReadSourceShardAsync(
                path,
                shard.GetProperty("actualRows").GetInt64(),
                selectedSources,
                result,
                ct
            );
        }
        return (result, artifact);
    }

    private static async Task ReadSourceShardAsync(
        string path,
        long expectedRows,
        HashSet<Guid> selectedSources,
        CatalogIndex catalog,
        CancellationToken ct
    )
    {
        await using var file = File.OpenRead(path);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        long line = 0;
        long rows = 0;
        var header = false;
        var complete = false;
        while (await reader.ReadLineAsync(ct) is { } text)
        {
            line++;
            using var document = JsonDocument.Parse(text);
            var record = document.RootElement;
            var kind = record.GetProperty("kind").GetString();
            if (complete || !header && kind != "snapshot")
            {
                throw new InvalidDataException("Catalog source shard framing differs.");
            }
            if (kind == "snapshot")
            {
                if (
                    header
                    || record.GetProperty("read_only").GetString() != "on"
                    || !record.GetProperty("in_recovery").GetBoolean()
                )
                {
                    throw new InvalidDataException(
                        "Expected an original read-only replica source snapshot."
                    );
                }
                header = true;
            }
            else if (kind == "artist")
            {
                var row = record.GetProperty("row");
                var artist = new CatalogArtist(
                    row.GetProperty("id").GetInt64(),
                    row.GetProperty("uuid").GetGuid(),
                    row.GetProperty("name").GetString()!
                );
                if (
                    catalog.Artists.TryGetValue(artist.Uuid, out var prior) && prior.Id != artist.Id
                    || catalog.ArtistUuids.TryGetValue(artist.Id, out var priorUuid)
                        && priorUuid != artist.Uuid
                )
                {
                    throw new InvalidDataException("Catalog artist namespaces conflict.");
                }
                catalog.Artists.TryAdd(artist.Uuid, artist);
                catalog.ArtistUuids.TryAdd(artist.Id, artist.Uuid);
            }
            else if (kind == "source_sample")
            {
                rows++;
                var row = record.GetProperty("row");
                var uuid = row.GetProperty("uuid").GetGuid();
                if (selectedSources.Contains(uuid))
                {
                    var source = new CatalogSource(
                        row.Clone(),
                        record.TryGetProperty("show_context", out var show)
                        && show.ValueKind == JsonValueKind.Object
                            ? show.Clone()
                            : null,
                        path,
                        line
                    );
                    if (!catalog.Sources.TryAdd(uuid, source))
                    {
                        catalog.Conflicts.Add(uuid);
                    }
                }
            }
            else if (kind == "snapshot_complete")
            {
                complete = true;
                if (record.GetProperty("shard_rows").GetInt64() != rows)
                {
                    throw new InvalidDataException("Catalog source footer row count differs.");
                }
            }
            else if (kind != "totals")
            {
                throw new InvalidDataException("Unexpected record in a catalog source shard.");
            }
        }
        if (!header || !complete || rows != expectedRows)
        {
            throw new InvalidDataException("Catalog source shard is incomplete.");
        }
    }

    private static RetainedHttpCatalogBinding BindSource(
        HttpItem item,
        NormalizationResourceKey resource,
        CatalogIndex catalog,
        RetainedHttpArtifact manifest,
        out CatalogSource? source
    )
    {
        catalog.Sources.TryGetValue(item.SourceUuid, out source);
        string? reason =
            source == null ? "missing_catalog_source"
            : catalog.Conflicts.Contains(item.SourceUuid) ? "conflicting_catalog_source"
            : null;
        if (source != null)
        {
            if (
                !catalog.Artists.TryGetValue(item.ArtistUuid, out var artist)
                || source.Row.GetProperty("artist_id").GetInt64() != artist.Id
            )
            {
                reason = "source_artist_conflict";
            }
            else if (
                source.Row.GetProperty("upstream_identifier").GetString() != resource.ProviderKey
            )
            {
                reason = "source_provider_identifier_conflict";
            }
        }
        var show = reason == null ? source?.Show : null;
        var showValid =
            show is { } value
            && source != null
            && source.Row.TryGetProperty("show_id", out var showId)
            && showId.ValueKind == JsonValueKind.Number
            && value.GetProperty("id").GetInt64() == showId.GetInt64()
            && value.GetProperty("artist_id").GetInt64()
                == source.Row.GetProperty("artist_id").GetInt64();
        return new(
            reason == null ? "bound" : "unresolved",
            reason,
            item.SourceUuid,
            reason == null ? item.SourceUuid : null,
            showValid ? show!.Value.GetProperty("uuid").GetGuid() : null,
            reason != null ? "recording_binding_unresolved"
                : showValid ? null
                : "missing_or_conflicting_catalog_show",
            manifest,
            source?.ShardPath,
            source?.Line
        );
    }

    private sealed class CatalogIndex
    {
        public Dictionary<Guid, CatalogArtist> Artists { get; } = [];
        public Dictionary<long, Guid> ArtistUuids { get; } = [];
        public Dictionary<Guid, CatalogSource> Sources { get; } = [];
        public HashSet<Guid> Conflicts { get; } = [];
    }

    private sealed record CatalogArtist(long Id, Guid Uuid, string Name);

    private sealed record CatalogSource(
        JsonElement Row,
        JsonElement? Show,
        string ShardPath,
        long Line
    );
}
