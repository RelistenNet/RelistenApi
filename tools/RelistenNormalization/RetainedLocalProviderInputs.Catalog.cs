using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Relisten.Api.Models.Normalization;
using static Relisten.Normalization.Batch.RetainedHttpProviderInputs;

namespace Relisten.Normalization.Batch;

public sealed partial class RetainedLocalProviderInputs
{
    // Scan each authenticated raw shard once. Keep exact URL matches, then read only their
    // source bodies; decoded provider dates never narrow or replace the ownership lookup.
    private static async Task<LocalCatalog> ReadCatalogAsync(
        string manifestPath,
        HashSet<string> wantedUrls,
        string root,
        CancellationToken ct
    )
    {
        var (manifest, artifact) = await ReadJsonAsync(manifestPath, ct);
        if (!manifest.GetProperty("complete").GetBoolean())
        {
            throw new InvalidDataException("Retained catalog manifest is incomplete.");
        }
        var catalog = new LocalCatalog(artifact);
        var directory = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        var shards = manifest.GetProperty("completedShards").EnumerateArray().ToArray();
        foreach (
            var shard in shards.Where(shard =>
                shard
                    .GetProperty("shardId")
                    .GetString()!
                    .Contains("-files-", StringComparison.Ordinal)
            )
        )
        {
            await ReadShardAsync(
                directory,
                shard,
                "track_sample",
                (row, path, line) =>
                {
                    var url = Text(row.GetProperty("row"), "mp3_url");
                    if (url != null && wantedUrls.Contains(url))
                    {
                        if (++catalog.MatchedFileRows > 100_000)
                        {
                            throw new InvalidDataException(
                                "Local catalog media matches exceed 100,000 rows."
                            );
                        }
                        if (!catalog.Media.TryGetValue(url, out var rows))
                        {
                            rows = [];
                            catalog.Media.Add(url, rows);
                        }
                        rows.Add(new(row.Clone(), path, line));
                    }
                },
                null,
                ct
            );
        }
        var sourceIds = catalog
            .Media.Values.SelectMany(rows => rows)
            .Select(row => Number(row.Record.GetProperty("row"), "source_id"))
            .OfType<long>()
            .ToHashSet();
        foreach (
            var shard in shards.Where(shard =>
                shard
                    .GetProperty("shardId")
                    .GetString()!
                    .Contains("-sources-", StringComparison.Ordinal)
            )
        )
        {
            await ReadShardAsync(
                directory,
                shard,
                "source_sample",
                (record, path, line) =>
                {
                    var row = record.GetProperty("row");
                    if (sourceIds.Contains(row.GetProperty("id").GetInt64()))
                    {
                        if (
                            !catalog.Sources.TryAdd(
                                row.GetProperty("id").GetInt64(),
                                new(
                                    row.Clone(),
                                    Object(record, "show_context")?.Clone(),
                                    path,
                                    line
                                )
                            )
                        )
                        {
                            throw new InvalidDataException(
                                "Retained source shards contain duplicate source IDs."
                            );
                        }
                    }
                },
                record => ReadArtist(catalog, record, root),
                ct
            );
        }
        return catalog;
    }

    private static void ReadArtist(LocalCatalog catalog, JsonElement record, string root)
    {
        var row = record.GetProperty("row");
        var uuid = row.GetProperty("uuid").GetGuid();
        var id = row.GetProperty("id").GetInt64();
        if (catalog.ArtistUuids.TryGetValue(id, out var previousUuid) && previousUuid != uuid)
        {
            throw new InvalidDataException("Captured artist IDs and UUIDs conflict.");
        }
        catalog.ArtistUuids[id] = uuid;
        var mapped = record
            .GetProperty("configured_provider_mappings")
            .EnumerateArray()
            .Any(mapping =>
                Text(mapping, "provider_name") == "local"
                && Text(mapping, "upstream_identifier") == root
            );
        if (
            catalog.Artists.TryGetValue(uuid, out var prior)
            && (prior.Id != id || prior.Mapped != mapped)
        )
        {
            throw new InvalidDataException(
                "Captured Local artist mappings disagree across catalog shards."
            );
        }
        catalog.Artists[uuid] = new(id, uuid, row.GetProperty("name").GetString()!, mapped);
    }

    private static async Task ReadShardAsync(
        string directory,
        JsonElement shard,
        string payloadKind,
        Action<JsonElement, string, long> accept,
        Action<JsonElement>? artist,
        CancellationToken ct
    )
    {
        var path = ContainedPath(directory, shard.GetProperty("gzipPath").GetString()!);
        var declared = shard.GetProperty("compressed");
        await using var file = File.OpenRead(path);
        using var digest = SHA256.Create();
        await using var authenticated = new CryptoStream(
            file,
            digest,
            CryptoStreamMode.Read,
            leaveOpen: true
        );
        long line = 0,
            rows = 0;
        var header = false;
        var complete = false;
        await using (
            var gzip = new GZipStream(authenticated, CompressionMode.Decompress, leaveOpen: true)
        )
        using (var reader = new StreamReader(gzip))
        {
            while (await reader.ReadLineAsync(ct) is { } text)
            {
                ct.ThrowIfCancellationRequested();
                line++;
                using var document = JsonDocument.Parse(text);
                var record = document.RootElement;
                var kind = record.GetProperty("kind").GetString();
                if (complete || !header && kind != "snapshot")
                {
                    throw new InvalidDataException("Retained catalog shard framing differs.");
                }
                if (kind == "snapshot")
                {
                    if (
                        header
                        || Text(record, "read_only") != "on"
                        || !record.GetProperty("in_recovery").GetBoolean()
                    )
                    {
                        throw new InvalidDataException(
                            "Expected an original read-only replica shard."
                        );
                    }
                    header = true;
                }
                else if (kind == "artist")
                {
                    artist?.Invoke(record);
                }
                else if (kind == payloadKind)
                {
                    rows++;
                    accept(record, path, line);
                }
                else if (kind == "snapshot_complete")
                {
                    complete = true;
                    if (record.GetProperty("shard_rows").GetInt64() != rows)
                    {
                        throw new InvalidDataException(
                            "Retained catalog footer row count differs."
                        );
                    }
                }
                else if (kind != "totals")
                {
                    throw new InvalidDataException("Unexpected catalog shard row.");
                }
            }
        }
        await authenticated.CopyToAsync(Stream.Null, ct);
        if (
            !header
            || !complete
            || rows != shard.GetProperty("actualRows").GetInt64()
            || file.Length != declared.GetProperty("bytes").GetInt64()
            || !StringComparer.OrdinalIgnoreCase.Equals(
                Convert.ToHexStringLower(digest.Hash!),
                declared.GetProperty("sha256").GetString()
            )
        )
        {
            throw new InvalidDataException(
                "Retained catalog shard differs from its completed manifest."
            );
        }
    }

    private static (RetainedLocalCatalogBinding Binding, LocalSource? Source) Bind(
        LocalCapture capture,
        DecodedDirectory directory,
        ProviderDecodeResult rootDecoded,
        LocalCatalog catalog
    )
    {
        var accounts = rootDecoded
            .Recordings.Where(account => account.Origin.Item == directory.Capture.Input.Resource)
            .ToArray();
        var mappings = catalog
            .Artists.Values.Where(artist => artist.Mapped)
            .Select(artist => new RetainedLocalArtistMapping(
                artist.Id,
                artist.Uuid,
                capture.Root.Resource.ProviderKey
            ))
            .OrderBy(mapping => mapping.ArtistUuid)
            .ToArray();
        var rootIssue =
            accounts.Length == 0 ? "directory_not_in_root"
            : accounts.Length > 1 ? "multiple_directory_accounts"
            : mappings.Length == 0 ? "missing_root_artist"
            : mappings.Length > 1 ? "multiple_root_artists"
            : mappings[0].ArtistUuid != capture.ArtistUuid ? "artist_mismatch"
            : null;
        var (anchors, candidateIds) = BindFiles(
            capture,
            directory,
            catalog,
            rootIssue,
            mappings.Length == 1 ? mappings[0].ArtistId : null
        );

        var sourceIssue =
            rootIssue
            ?? (
                candidateIds.Count == 0
                    ? anchors.Any(anchor => anchor.Reason == "file_ownership_conflict")
                        ? "file_ownership_conflict"
                        : "missing_directory_media"
                    : candidateIds.Count > 1
                        ? "multiple_directory_recordings"
                        : null
            );
        var bound = sourceIssue == null ? catalog.Sources[candidateIds.Single()] : null;
        var sourceUuid = bound == null ? null : Uuid(bound.Row, "uuid");
        var show = bound?.Show;
        var validShow =
            show != null
            && Number(show.Value, "id") == Number(bound!.Row, "show_id")
            && Number(show.Value, "artist_id") == Number(bound.Row, "artist_id")
            && Uuid(show.Value, "uuid") != null;
        // The date-selected UUID belongs to sampling, not a reviewed assignment. Exact
        // media ownership can expose that hint's error without moving any catalog file.
        var binding = new RetainedLocalCatalogBinding(
            sourceIssue == null ? "bound" : "unresolved",
            sourceIssue,
            directory.Capture.ExpectedSourceUuid,
            sourceUuid != null
                && directory.Capture.ExpectedSourceUuid != null
                && sourceUuid != directory.Capture.ExpectedSourceUuid,
            sourceUuid,
            validShow ? Uuid(show!.Value, "uuid") : null,
            sourceIssue != null ? "recording_binding_unresolved"
                : validShow ? null
                : "missing_or_conflicting_catalog_show",
            candidateIds
                .Select(id => catalog.Sources[id].Row.GetProperty("uuid").GetGuid())
                .Order()
                .ToArray(),
            mappings,
            anchors,
            catalog.Manifest,
            bound?.Path,
            bound?.Line
        );
        return (binding, bound);
    }

    private static (
        IReadOnlyList<RetainedLocalFileAnchor> Anchors,
        HashSet<long> CandidateSourceIds
    ) BindFiles(
        LocalCapture capture,
        DecodedDirectory directory,
        LocalCatalog catalog,
        string? rootIssue,
        long? artistId
    )
    {
        var files = directory.Decoded.Recordings.Single().Files;
        var anchors = new List<RetainedLocalFileAnchor>();
        var candidateIds = new HashSet<long>();
        var duplicates = files
            .Select(file =>
                MediaUrl(capture.Root.Resource.ProviderKey, directory.Capture.Directory, file)
            )
            .OfType<string>()
            .GroupBy(url => url, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        for (var index = 0; index < files.Count; index++)
        {
            var url = MediaUrl(
                capture.Root.Resource.ProviderKey,
                directory.Capture.Directory,
                files[index]
            );
            var rows = url == null ? [] : catalog.Media.GetValueOrDefault(url) ?? [];
            var valid =
                rootIssue == null
                    ? rows.Where(row =>
                            OwnershipIssue(row.Record, catalog, artistId!.Value) == null
                        )
                        .ToArray()
                    : [];
            foreach (var match in valid)
            {
                candidateIds.Add(
                    match.Record.GetProperty("row").GetProperty("source_id").GetInt64()
                );
            }
            var reason =
                rootIssue
                ?? (
                    url == null ? "missing_media_url"
                    : duplicates.Contains(url) ? "duplicate_file_identity"
                    : rows.Count == 0 ? "missing_directory_media"
                    : valid.Length == 0 ? "file_ownership_conflict"
                    : rows.Count > 1 ? "multiple_media_files"
                    : null
                );
            var selected = reason == null ? valid.Single() : null;
            var row = selected?.Record.GetProperty("row");
            var source =
                row == null ? null : catalog.Sources[row.Value.GetProperty("source_id").GetInt64()];
            anchors.Add(
                new(
                    index,
                    url,
                    reason == null ? "bound" : "unresolved",
                    reason,
                    row == null ? null : Uuid(row.Value, "uuid"),
                    source == null ? null : Uuid(source.Row, "uuid"),
                    selected == null
                        ? null
                        : Uuid(selected.Record.GetProperty("set_context"), "uuid"),
                    selected?.Path,
                    selected?.Line
                )
            );
        }

        return (anchors, candidateIds);
    }

    private static string? OwnershipIssue(JsonElement record, LocalCatalog catalog, long artist)
    {
        var track = record.GetProperty("row");
        var sourceId = Number(track, "source_id");
        if (
            sourceId == null
            || !catalog.Sources.TryGetValue(sourceId.Value, out var source)
            || Number(track, "artist_id") != artist
            || Number(source.Row, "artist_id") != artist
            || Uuid(source.Row, "uuid") is not { } sourceUuid
            || sourceUuid == Guid.Empty
            || !track.TryGetProperty("is_orphaned", out var orphaned)
            || orphaned.ValueKind != JsonValueKind.False
            || Uuid(track, "uuid") is not { } trackUuid
            || trackUuid == Guid.Empty
        )
        {
            return "file_ownership_conflict";
        }
        var set = Object(record, "set_context");
        var direct = Object(record, "source_context");
        return
            set == null
            || direct == null
            || Uuid(set.Value, "uuid") is not { } setUuid
            || setUuid == Guid.Empty
            || Number(track, "source_set_id") != Number(set.Value, "id")
            || Number(set.Value, "source_id") != sourceId
            || Number(direct.Value, "id") != sourceId
            || Uuid(direct.Value, "uuid") != Uuid(source.Row, "uuid")
            || Number(direct.Value, "artist_id") != artist
            || Number(direct.Value, "show_id") != Number(source.Row, "show_id")
            ? "file_ownership_conflict"
            : null;
    }

    private static JsonElement? Object(JsonElement value, string name) =>
        value.TryGetProperty(name, out var found) && found.ValueKind == JsonValueKind.Object
            ? found
            : null;

    private static string? Text(JsonElement value, string name) =>
        value.TryGetProperty(name, out var found) && found.ValueKind == JsonValueKind.String
            ? found.GetString()
            : null;

    private static long? Number(JsonElement value, string name) =>
        value.TryGetProperty(name, out var found)
        && found.ValueKind == JsonValueKind.Number
        && found.TryGetInt64(out var number)
            ? number
            : null;

    private static Guid? Uuid(JsonElement value, string name) =>
        value.TryGetProperty(name, out var found)
        && found.ValueKind == JsonValueKind.String
        && found.TryGetGuid(out var uuid)
            ? uuid
            : null;

    private sealed class LocalCatalog(RetainedHttpArtifact manifest)
    {
        internal RetainedHttpArtifact Manifest { get; } = manifest;
        internal Dictionary<string, List<LocalMedia>> Media { get; } = new(StringComparer.Ordinal);
        internal Dictionary<long, LocalSource> Sources { get; } = [];
        internal Dictionary<Guid, LocalArtist> Artists { get; } = [];
        internal Dictionary<long, Guid> ArtistUuids { get; } = [];
        internal long MatchedFileRows { get; set; }
    }

    private sealed record LocalMedia(JsonElement Record, string Path, long Line);

    private sealed record LocalSource(JsonElement Row, JsonElement? Show, string Path, long Line);

    private sealed record LocalArtist(long Id, Guid Uuid, string Name, bool Mapped);
}
