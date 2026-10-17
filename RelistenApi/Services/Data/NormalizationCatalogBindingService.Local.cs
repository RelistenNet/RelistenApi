using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationCatalogBindingService
{
    private const int MaximumLocalRootArtists = 10_000;

    /// <summary>
    /// Binds one Local directory using its retained root account and exact current media ownership.
    /// Dates do not select a source. Both input revisions and all returned catalog dependencies are
    /// required publication inputs, including registry/artist fences on unresolved outcomes.
    /// </summary>
    public Task<NormalizationRecordingBindingSnapshot> BindLocalDirectoryAsync(
        NormalizationInputRevision rootInput,
        NormalizationInputRevision directoryInput,
        Guid? expectedArtistUuid = null,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (expectedArtistUuid == Guid.Empty)
        {
            throw new ArgumentException(
                "Expected artist UUID cannot be empty.",
                nameof(expectedArtistUuid)
            );
        }
        if (rootInput.Uuid == Guid.Empty || directoryInput.Uuid == Guid.Empty)
        {
            throw new ArgumentException("Both retained input revision UUIDs are required.");
        }
        if (
            rootInput.Resource.Provider != "local"
            || rootInput.Resource.ResourceType != "metadata_root"
            || directoryInput.Resource.Provider != "local"
            || directoryInput.Resource.ResourceType != "directory_metadata"
        )
        {
            throw new NotSupportedException(
                "Local directory binding requires its root and directory representations."
            );
        }

        var directory = LocalDirectoryParts(directoryInput.Resource);
        var decoder = new NormalizationProviderDecoder();
        var accounts = decoder
            .Decode(rootInput, cancellationToken)
            .Recordings.Where(account => account.Origin.Item == directoryInput.Resource)
            .ToArray();
        var recording = decoder.Decode(directoryInput, cancellationToken).Recordings.Single();
        if (recording.Files.Count > MaximumFiles)
        {
            throw new ArgumentException(
                "Recording binding exceeds its file limit.",
                nameof(directoryInput)
            );
        }

        return WithCatalogSnapshotAsync(
            async (c, t, ct) =>
            {
                var registry = await ReadLocalMappingRegistryAsync(c, t, ct);
                var artists = await ReadLocalRootArtistsAsync(
                    c,
                    t,
                    rootInput.Resource.ProviderKey,
                    ct
                );
                var clocks = await ReadLocalArtistClocksAsync(c, t, artists, ct);
                var preliminary = LocalContextIssue(
                    rootInput,
                    directory,
                    accounts,
                    artists,
                    expectedArtistUuid
                );
                SourceResolution source;
                FileRow[] rows = [];
                if (preliminary != null)
                {
                    source = preliminary;
                }
                else
                {
                    var urls = recording
                        .Files.Select(file => LocalMediaUrl(directory, file))
                        .OfType<string>()
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
                    rows = await ReadLocalMediaRowsAsync(c, t, artists[0].ArtistId, urls, ct);
                    source = await ResolveLocalSourceAsync(c, t, artists[0], rows, ct);
                }

                var files = BindLocalFiles(recording, directory, source, rows);
                return new NormalizationRecordingBindingSnapshot(
                    directoryInput.Uuid,
                    directoryInput.Resource,
                    source.Binding,
                    files,
                    clocks,
                    [],
                    [new(rootInput.Uuid, rootInput.Resource)],
                    registry
                );
            },
            cancellationToken
        );
    }

    private static LocalDirectoryIdentity LocalDirectoryParts(NormalizationResourceKey resource)
    {
        string[]? parts;
        try
        {
            parts = JsonSerializer.Deserialize<string[]>(resource.ProviderKey);
        }
        catch (JsonException)
        {
            throw new ArgumentException("Local directory resource has an invalid identity.");
        }
        if (
            parts is not { Length: 2 }
            || parts.Any(string.IsNullOrWhiteSpace)
            || NormalizationProviderResources.LocalDirectory(parts[0], parts[1]) != resource
        )
        {
            throw new ArgumentException("Local directory resource has an invalid identity.");
        }

        return new(parts[0], parts[1]);
    }

    private static SourceResolution? LocalContextIssue(
        NormalizationInputRevision root,
        LocalDirectoryIdentity identity,
        ProviderRecordingObservation[] accounts,
        LocalRootArtist[] artists,
        Guid? expectedArtist
    )
    {
        if (identity.Root != root.Resource.ProviderKey)
        {
            return UnresolvedSource(
                NormalizationCatalogBindingStatus.ProviderConflict,
                "directory_root_identity_mismatch"
            );
        }
        if (accounts.Length == 0)
        {
            return UnresolvedSource(
                NormalizationCatalogBindingStatus.Missing,
                "directory_not_in_root"
            );
        }
        if (accounts.Length > 1)
        {
            return UnresolvedSource(
                NormalizationCatalogBindingStatus.Ambiguous,
                "multiple_directory_accounts"
            );
        }
        if (artists.Length == 0)
        {
            return UnresolvedSource(
                NormalizationCatalogBindingStatus.Missing,
                "missing_root_artist"
            );
        }
        if (artists.Length > 1)
        {
            return UnresolvedSource(
                NormalizationCatalogBindingStatus.Ambiguous,
                "multiple_root_artists"
            );
        }
        if (expectedArtist != null && artists[0].ArtistUuid != expectedArtist)
        {
            return UnresolvedSource(
                NormalizationCatalogBindingStatus.ProviderConflict,
                "artist_mismatch"
            );
        }

        return null;
    }

    private static async Task<NormalizationMappingRegistryDependency> ReadLocalMappingRegistryAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        CancellationToken ct
    )
    {
        var generation = await c.QuerySingleOrDefaultAsync<long?>(
            new CommandDefinition(
                "SELECT generation FROM normalization.catalog_mapping_registry_revision WHERE singleton",
                transaction: t,
                cancellationToken: ct
            )
        );
        return generation == null
            ? throw new InvalidOperationException(
                "The catalog mapping registry has no revision fence."
            )
            : new(generation.Value);
    }

    private static async Task<LocalRootArtist[]> ReadLocalRootArtistsAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        string root,
        CancellationToken ct
    )
    {
        var rows = (
            await c.QueryAsync<LocalRootArtist>(
                new CommandDefinition(
                    """
                    SELECT DISTINCT a.id AS ArtistId,a.uuid AS ArtistUuid
                    FROM artists_upstream_sources mapping JOIN upstream_sources provider ON provider.id=mapping.upstream_source_id
                    JOIN artists a ON a.id=mapping.artist_id
                    WHERE provider.name='local' AND mapping.upstream_identifier=@root
                    ORDER BY a.uuid LIMIT @limit
                    """,
                    new { root, limit = MaximumLocalRootArtists + 1 },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        if (rows.Length > MaximumLocalRootArtists)
        {
            throw new InvalidOperationException("Local root artist lookup exceeds its limit.");
        }

        return rows;
    }

    private static async Task<NormalizationCatalogDependency[]> ReadLocalArtistClocksAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        LocalRootArtist[] artists,
        CancellationToken ct
    )
    {
        var rows = (
            await c.QueryAsync<NormalizationCatalogDependency>(
                new CommandDefinition(
                    "SELECT artist_uuid AS ArtistUuid,generation AS Generation FROM normalization.catalog_artist_revisions WHERE artist_uuid=ANY(@artists) ORDER BY artist_uuid",
                    new { artists = artists.Select(artist => artist.ArtistUuid).ToArray() },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        if (rows.Length != artists.Length)
        {
            throw new InvalidOperationException(
                "A configured Local artist has no catalog revision fence."
            );
        }

        return rows;
    }

    private static async Task<FileRow[]> ReadLocalMediaRowsAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        int artist,
        string[] urls,
        CancellationToken ct
    )
    {
        if (urls.Length == 0)
        {
            return [];
        }

        // Normal associations use the (artist_id,mp3_url) index. The second bounded branch
        // retains corrupt track-artist associations owned by this artist's sources for diagnosis.
        var rows = (
            await c.QueryAsync<FileRow>(
                new CommandDefinition(
                    """
                    WITH selected AS MATERIALIZED (
                        (SELECT tr.id FROM source_tracks tr WHERE tr.artist_id=@artist AND tr.mp3_url=ANY(@urls) LIMIT @limit)
                        UNION ALL
                        (SELECT tr.id FROM sources s JOIN source_tracks tr ON tr.source_id=s.id
                            WHERE s.artist_id=@artist AND tr.artist_id IS DISTINCT FROM @artist AND tr.mp3_url=ANY(@urls) LIMIT @limit)
                    )
                    SELECT tr.uuid AS TrackUuid,tr.source_id AS SourceId,tr.artist_id AS ArtistId,
                        tr.mp3_url AS MediaUrl,tr.is_orphaned AS Orphaned,ss.uuid AS SourceSetUuid,ss.source_id AS SetSourceId
                    FROM selected JOIN source_tracks tr ON tr.id=selected.id
                    LEFT JOIN source_sets ss ON ss.id=tr.source_set_id LIMIT @limit
                    """,
                    new
                    {
                        artist,
                        urls,
                        limit = MaximumFiles + 1,
                    },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        if (rows.Length > MaximumFiles)
        {
            throw new InvalidOperationException("Local media lookup exceeds its file limit.");
        }

        return rows;
    }

    private static async Task<SourceResolution> ResolveLocalSourceAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        LocalRootArtist artist,
        FileRow[] files,
        CancellationToken ct
    )
    {
        var sources = (
            await c.QueryAsync<SourceRow>(
                new CommandDefinition(
                    $"SELECT {SourceColumns} {SourceJoins} WHERE s.id=ANY(@ids)",
                    new
                    {
                        ids = files
                            .Select(file => file.SourceId)
                            .OfType<long>()
                            .Distinct()
                            .ToArray(),
                    },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToDictionary(source => source.SourceId);
        var candidates = files
            .Where(file =>
                file.SourceId is { } sourceId
                && sources.TryGetValue(sourceId, out var source)
                && source.ArtistId == artist.ArtistId
                && FileOwnershipIssue(source, file) == null
            )
            .Select(file => file.SourceId)
            .OfType<long>()
            .Distinct()
            .ToArray();
        if (candidates.Length > 1)
        {
            return UnresolvedSource(
                NormalizationCatalogBindingStatus.Ambiguous,
                "multiple_directory_recordings"
            );
        }
        if (candidates.Length == 1)
        {
            return ResolveSource(
                sources[candidates[0]],
                artist.ArtistUuid,
                "qualified_local_directory_media"
            );
        }
        var conflict = files.Any(file =>
            file.SourceId is not { } sourceId
            || !sources.TryGetValue(sourceId, out var source)
            || source.ArtistId != artist.ArtistId
            || FileOwnershipIssue(source, file)?.Status
                == NormalizationCatalogBindingStatus.ProviderConflict
        );
        return UnresolvedSource(
            conflict
                ? NormalizationCatalogBindingStatus.ProviderConflict
                : NormalizationCatalogBindingStatus.Missing,
            conflict ? "file_ownership_conflict" : "missing_directory_media"
        );
    }

    private static NormalizationFileBinding[] BindLocalFiles(
        ProviderRecordingObservation recording,
        LocalDirectoryIdentity directory,
        SourceResolution source,
        FileRow[] rows
    )
    {
        var duplicates = recording
            .Files.Select(file => LocalMediaUrl(directory, file))
            .Where(url => url != null)
            .GroupBy(url => url!, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        return recording
            .Files.Select(file =>
            {
                NormalizationCatalogBinding<NormalizationCatalogFileReference> binding;
                if (source.Row == null)
                {
                    binding = UnresolvedFile(source.Binding.Status, source.Binding.Reason);
                }
                else if (LocalMediaUrl(directory, file) is not { } url)
                {
                    binding = UnresolvedFile(
                        NormalizationCatalogBindingStatus.Missing,
                        "missing_media_url"
                    );
                }
                else if (duplicates.Contains(url))
                {
                    binding = UnresolvedFile(
                        NormalizationCatalogBindingStatus.Ambiguous,
                        "duplicate_file_identity"
                    );
                }
                else
                {
                    binding = BindExactMedia(source.Row, rows, url);
                }
                return new NormalizationFileBinding(file, binding);
            })
            .ToArray();
    }

    private static string? LocalMediaUrl(
        LocalDirectoryIdentity directory,
        ProviderFileObservation file
    ) =>
        string.IsNullOrWhiteSpace(file.PlaybackLocator?.Value)
            ? null
            : $"https://audio.relisten.net/{directory.Root}/{directory.Directory}/{file.PlaybackLocator.Value}";

    private sealed record LocalDirectoryIdentity(string Root, string Directory);

    private sealed record LocalRootArtist(int ArtistId, Guid ArtistUuid);
}
