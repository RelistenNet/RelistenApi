using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

/// <summary>
/// Resolves decoded recording/file assertions to existing catalog UUIDs. Provider qualification,
/// ownership and all returned catalog clocks come from one read-only primary snapshot. This
/// service does not allocate identities, change assignments or accept normalized interpretations.
/// </summary>
public sealed partial class NormalizationCatalogBindingService(DbService db)
{
    private const int MaximumFiles = 10_000;

    /// <summary>
    /// Binds one decoded legacy, Archive or Phish.in recording and its files. Global provider ambiguity is
    /// checked before expectedArtistUuid. Missing derived show/venue context leaves source credits
    /// usable; invalid or absent file relationships remain explicit unresolved outcomes.
    /// </summary>
    /// <remarks>
    /// Call outside catalog transactions. Include returned CatalogInputs, SourceIdentities and
    /// SourceUuids in the publication ticket, including unresolved outcomes. Artist clocks fence
    /// current row ownership; identifier clocks fence another artist acquiring the same provider
    /// item after the global lookup. UUID clocks also retain missing exact-source observations.
    /// Input citations are decoder output and remain subject to publication evidence validation.
    /// Rebinding refreshes associations only. It does not refresh retained wording: processing a
    /// changed legacy credit or title also requires a new catalog snapshot and fresh decoding.
    /// </remarks>
    public Task<NormalizationRecordingBindingSnapshot> BindRecordingAsync(
        ProviderRecordingObservation recording,
        Guid? expectedArtistUuid = null,
        CancellationToken cancellationToken = default
    )
    {
        ValidateRecordingRequest(recording, expectedArtistUuid, cancellationToken);
        return WithCatalogSnapshotAsync(
            (connection, transaction, ct) =>
                BindRecordingSnapshotAsync(
                    connection,
                    transaction,
                    recording,
                    expectedArtistUuid,
                    ct
                ),
            cancellationToken
        );
    }

    private Task<T> WithCatalogSnapshotAsync<T>(
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<T>> read,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Transaction.Current != null)
        {
            throw new InvalidOperationException(
                "Read catalog bindings outside a catalog transaction."
            );
        }
        return db.WithWriteConnection(async connection =>
        {
            var primary = (NpgsqlConnection)connection;
            await using var transaction = await primary.BeginTransactionAsync(
                System.Data.IsolationLevel.RepeatableRead,
                cancellationToken
            );
            await connection.ExecuteAsync(
                new CommandDefinition(
                    "SET TRANSACTION READ ONLY",
                    transaction: transaction,
                    cancellationToken: cancellationToken
                )
            );
            var result = await read(primary, transaction, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        });
    }

    private static void ValidateRecordingRequest(
        ProviderRecordingObservation recording,
        Guid? expectedArtistUuid,
        CancellationToken cancellationToken
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
        if (recording.Origin.Evidence.InputRevisionUuid == Guid.Empty)
        {
            throw new ArgumentException(
                "Recording input revision UUID is required.",
                nameof(recording)
            );
        }
        if (recording.Files.Count > MaximumFiles)
        {
            throw new ArgumentException(
                "Recording binding exceeds its file limit.",
                nameof(recording)
            );
        }
        if (
            recording.Files.Any(file =>
                file.Origin.Evidence.InputRevisionUuid
                != recording.Origin.Evidence.InputRevisionUuid
            )
        )
        {
            throw new ArgumentException(
                "File observations must belong to the recording's input revision.",
                nameof(recording)
            );
        }
    }

    private static async Task<NormalizationRecordingBindingSnapshot> BindRecordingSnapshotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ProviderRecordingObservation recording,
        Guid? expectedArtistUuid,
        CancellationToken cancellationToken
    )
    {
        var source = await ResolveRecordingAsync(
            connection,
            transaction,
            recording.Origin.Item,
            expectedArtistUuid,
            cancellationToken
        );
        var files = await BindFilesAsync(
            connection,
            transaction,
            recording,
            source,
            cancellationToken
        );
        var fences = await ReadCatalogFencesAsync(
            connection,
            transaction,
            recording.Origin.Item,
            source,
            cancellationToken
        );
        return new NormalizationRecordingBindingSnapshot(
            recording.Origin.Evidence.InputRevisionUuid,
            recording.Origin.Item,
            source.Binding,
            files,
            fences.Artists,
            fences.SourceIdentities,
            SourceUuids: fences.SourceUuids.Count == 0 ? null : fences.SourceUuids
        );
    }

    private static async Task<CatalogFences> ReadCatalogFencesAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        NormalizationResourceKey resource,
        SourceResolution resolution,
        CancellationToken ct
    )
    {
        IReadOnlyList<NormalizationCatalogDependency> artists = [];
        if (resolution.Binding.Value is { } recording)
        {
            var generation = await c.QuerySingleOrDefaultAsync<long?>(
                new CommandDefinition(
                    "SELECT generation FROM normalization.catalog_artist_revisions WHERE artist_uuid=@ArtistUuid",
                    recording,
                    t,
                    cancellationToken: ct
                )
            );
            if (generation == null)
            {
                throw new InvalidOperationException(
                    "The bound artist has no catalog revision fence."
                );
            }
            artists = [new(recording.ArtistUuid, generation.Value)];
        }
        IReadOnlyList<NormalizationSourceIdentityDependency> sourceIdentities = [];
        if (resource.Provider is "archive.org" or "phish.in")
        {
            // A missing item still consumes the global lookup. Zero is the original absence
            // observation; admission creates its retained clock before comparing that value.
            var generation = await c.QuerySingleOrDefaultAsync<long?>(
                new CommandDefinition(
                    "SELECT generation FROM normalization.catalog_source_identity_revisions WHERE upstream_identifier=@ProviderKey",
                    resource,
                    t,
                    cancellationToken: ct
                )
            );
            if (generation == null && resolution.Binding.Value != null)
            {
                throw new InvalidOperationException(
                    "The bound provider item has no source identity revision fence."
                );
            }

            sourceIdentities = [new(resource.ProviderKey, generation ?? 0)];
        }
        var sourceUuids = (resolution.SourceUuidLookups ?? []).Distinct().Order().ToArray();
        NormalizationSourceUuidDependency[] uuidClocks =
            sourceUuids.Length == 0
                ? []
                : (
                    await c.QueryAsync<NormalizationSourceUuidDependency>(
                        new CommandDefinition(
                            """
                            SELECT requested.uuid AS "SourceUuid",COALESCE(revision.generation,0) AS "Generation"
                            FROM unnest(@sourceUuids::uuid[]) requested(uuid)
                            LEFT JOIN normalization.catalog_source_uuid_revisions revision ON revision.source_uuid=requested.uuid
                            ORDER BY requested.uuid
                            """,
                            new { sourceUuids },
                            t,
                            cancellationToken: ct
                        )
                    )
                ).ToArray();
        return new(artists, sourceIdentities, uuidClocks);
    }

    private static Task<SourceResolution> ResolveRecordingAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        NormalizationResourceKey resource,
        Guid? expectedArtist,
        CancellationToken ct
    ) =>
        (resource.Provider, resource.ResourceType) switch
        {
            ("legacy_catalog", "recording") => ResolveLegacyRecordingAsync(
                c,
                t,
                resource,
                expectedArtist,
                ct
            ),
            ("archive.org", "item_metadata") => ResolveArchiveRecordingAsync(
                c,
                t,
                resource,
                expectedArtist,
                ct
            ),
            ("phish.in", "show") => ResolvePhishRecordingAsync(c, t, resource, expectedArtist, ct),
            _ => throw new NotSupportedException(
                "Catalog recording binding does not support this provider resource."
            ),
        };

    private static SourceResolution ResolveSource(
        SourceRow? source,
        Guid? expectedArtist,
        string reason
    )
    {
        if (source == null)
        {
            return UnresolvedSource(NormalizationCatalogBindingStatus.Missing, "missing_recording");
        }
        if (source.ArtistUuid == null)
        {
            return UnresolvedSource(NormalizationCatalogBindingStatus.Missing, "missing_artist");
        }
        if (expectedArtist != null && source.ArtistUuid != expectedArtist)
        {
            return UnresolvedSource(
                NormalizationCatalogBindingStatus.ProviderConflict,
                "artist_mismatch"
            );
        }

        var context = new List<string>();
        if (source.ShowUuid == null)
        {
            context.Add(source.ShowExists ? "show_artist_conflict" : "missing_show");
        }
        if (source.SourceVenueUuid == null)
        {
            context.Add(
                source.SourceVenueExists ? "source_venue_artist_conflict" : "missing_source_venue"
            );
        }
        return new(
            new(
                NormalizationCatalogBindingStatus.Bound,
                new(
                    source.ArtistUuid.Value,
                    source.SourceUuid,
                    source.ShowUuid,
                    source.SourceVenueUuid,
                    context
                ),
                reason
            ),
            source
        );
    }

    private static SourceResolution UnresolvedSource(
        NormalizationCatalogBindingStatus status,
        string reason,
        IReadOnlyList<Guid>? sourceUuidLookups = null
    ) => new(new(status, null, reason), null, sourceUuidLookups);

    private static async Task<NormalizationFileBinding[]> BindFilesAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        ProviderRecordingObservation recording,
        SourceResolution resolution,
        CancellationToken ct
    )
    {
        if (resolution.Row == null)
        {
            return recording
                .Files.Select(file => new NormalizationFileBinding(
                    file,
                    new(resolution.Binding.Status, null, resolution.Binding.Reason)
                ))
                .ToArray();
        }
        var source = resolution.Row;
        var legacy = recording.Origin.Item.Provider == "legacy_catalog";
        Guid[] legacyUuids = legacy
            ? recording
                .Files.SelectMany(file => file.Identities)
                .Where(identity =>
                    identity.Identity.Provider == "legacy_catalog"
                    && identity.Identity.ResourceType == "track"
                )
                .Select(identity =>
                    Guid.TryParse(identity.Identity.ProviderKey, out var uuid) ? uuid : Guid.Empty
                )
                .Where(uuid => uuid != Guid.Empty)
                .Distinct()
                .ToArray()
            : [];
        var rows = (
            await c.QueryAsync<FileRow>(
                new CommandDefinition(
                    """
                    SELECT tr.uuid AS TrackUuid,tr.source_id AS SourceId,tr.artist_id AS ArtistId,
                        tr.mp3_url AS MediaUrl,tr.is_orphaned AS Orphaned,
                        ss.uuid AS SourceSetUuid,ss.source_id AS SetSourceId
                    FROM source_tracks tr LEFT JOIN source_sets ss ON ss.id=tr.source_set_id
                    WHERE tr.source_id=@SourceId OR tr.uuid=ANY(@legacyUuids)
                    ORDER BY tr.uuid LIMIT @limit
                    """,
                    new
                    {
                        source.SourceId,
                        legacyUuids,
                        limit = MaximumFiles + 1,
                    },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        if (rows.Length > MaximumFiles)
        {
            throw new InvalidOperationException(
                "Catalog recording exceeds the binding file limit."
            );
        }

        var duplicateKeys = recording
            .Files.SelectMany(file => FileKeys(file, recording.Origin.Item.Provider))
            .GroupBy(key => key, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        return recording
            .Files.Select(file => new NormalizationFileBinding(
                file,
                BindFile(file, recording.Origin.Item, source, rows, duplicateKeys)
            ))
            .ToArray();
    }

    private static IReadOnlyList<string> FileKeys(ProviderFileObservation file, string provider)
    {
        if (provider == "phish.in")
        {
            return PhishFileKeys(file);
        }

        var key =
            provider == "legacy_catalog"
                ? file
                    .Identities.FirstOrDefault(identity =>
                        identity.Identity.Provider == "legacy_catalog"
                        && identity.Identity.ResourceType == "track"
                    )
                    ?.Identity.ProviderKey
                : file.Filename?.Value;
        return key == null ? [] : [key];
    }

    private static NormalizationCatalogBinding<NormalizationCatalogFileReference> BindExactMedia(
        SourceRow source,
        FileRow[] rows,
        string url
    )
    {
        var candidates = rows.Where(row => row.MediaUrl == url).ToArray();
        if (candidates.Length == 0)
        {
            return UnresolvedFile(NormalizationCatalogBindingStatus.Missing, "missing_file");
        }
        var active = candidates
            .Where(candidate => FileOwnershipIssue(source, candidate) == null)
            .ToArray();
        if (active.Length > 1)
        {
            return UnresolvedFile(
                NormalizationCatalogBindingStatus.Ambiguous,
                "multiple_catalog_media_rows"
            );
        }
        if (active.Length == 1)
        {
            return BoundFile(source, active[0]);
        }
        return FileOwnershipIssue(source, candidates[0])!;
    }

    private static NormalizationCatalogBinding<NormalizationCatalogFileReference> BoundFile(
        SourceRow source,
        FileRow track
    ) =>
        new(
            NormalizationCatalogBindingStatus.Bound,
            new(
                source.ArtistUuid!.Value,
                source.SourceUuid,
                track.SourceSetUuid!.Value,
                track.TrackUuid,
                track.MediaUrl!
            ),
            "exact_catalog_media"
        );

    private static NormalizationCatalogBinding<NormalizationCatalogFileReference> UnresolvedFile(
        NormalizationCatalogBindingStatus status,
        string reason
    ) => new(status, null, reason);

    private const string SourceColumns = """
        s.id AS SourceId,s.uuid AS SourceUuid,s.artist_id AS ArtistId,s.upstream_identifier AS UpstreamIdentifier,
        a.uuid AS ArtistUuid,
        CASE WHEN sh.artist_id=s.artist_id THEN sh.uuid END AS ShowUuid,(sh.id IS NOT NULL) AS ShowExists,
        CASE WHEN v.artist_id=s.artist_id THEN v.uuid END AS SourceVenueUuid,(v.id IS NOT NULL) AS SourceVenueExists
        """;

    private const string SourceJoins = """
        FROM sources s LEFT JOIN artists a ON a.id=s.artist_id
        LEFT JOIN shows sh ON sh.id=s.show_id LEFT JOIN venues v ON v.id=s.venue_id
        """;

    private sealed record CatalogFences(
        IReadOnlyList<NormalizationCatalogDependency> Artists,
        IReadOnlyList<NormalizationSourceIdentityDependency> SourceIdentities,
        IReadOnlyList<NormalizationSourceUuidDependency> SourceUuids
    );

    private sealed record SourceResolution(
        NormalizationCatalogBinding<NormalizationCatalogRecordingReference> Binding,
        SourceRow? Row,
        IReadOnlyList<Guid>? SourceUuidLookups = null
    );

    private sealed record SourceRow(
        long SourceId,
        Guid SourceUuid,
        int ArtistId,
        string UpstreamIdentifier,
        Guid? ArtistUuid,
        Guid? ShowUuid,
        bool ShowExists,
        Guid? SourceVenueUuid,
        bool SourceVenueExists
    );

    private sealed record FileRow(
        Guid TrackUuid,
        long? SourceId,
        int? ArtistId,
        string? MediaUrl,
        bool Orphaned,
        Guid? SourceSetUuid,
        long? SetSourceId
    );
}
