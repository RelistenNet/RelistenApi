using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationCatalogBindingService
{
    private static async Task<SourceResolution> ResolveArchiveRecordingAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        NormalizationResourceKey resource,
        Guid? expectedArtist,
        CancellationToken ct
    )
    {
        // Detect duplicate provider-qualified recordings globally before applying the caller's
        // expected artist. Filtering first would conceal an unresolved catalog identity conflict.
        var matches = (
            await c.QueryAsync<SourceRow>(
                new CommandDefinition(
                    $"""
                    SELECT {SourceColumns} {SourceJoins}
                    WHERE s.upstream_identifier=@item AND EXISTS (
                        SELECT 1 FROM links l JOIN upstream_sources p ON p.id=l.upstream_source_id
                        WHERE l.source_id=s.id AND l.for_source AND p.name='archive.org')
                    ORDER BY s.uuid LIMIT 2
                    """,
                    new { item = resource.ProviderKey },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        if (matches.Length > 1)
        {
            return UnresolvedSource(
                NormalizationCatalogBindingStatus.Ambiguous,
                "multiple_provider_recordings"
            );
        }

        var retained = (
            await c.QueryAsync<ArchiveReference>(
                new CommandDefinition(
                    """
                    SELECT canonical_source_uuid AS SourceUuid,selected_artist_uuid AS ArtistUuid,(status='conflict') AS Conflict
                    FROM normalization.archive_item_resolutions WHERE item_identifier=@item
                    UNION ALL
                    SELECT published_source_uuid AS SourceUuid,published_artist_uuid AS ArtistUuid,false AS Conflict
                    FROM normalization.archive_item_intake WHERE item_identifier=@item AND published_source_uuid IS NOT NULL
                    """,
                    new { item = resource.ProviderKey },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        var sourceUuids = retained
            .Where(row => row.SourceUuid != null)
            .Select(row => row.SourceUuid!.Value)
            .Distinct()
            .ToArray();
        var artistUuids = retained
            .Where(row => row.ArtistUuid != null)
            .Select(row => row.ArtistUuid!.Value)
            .Distinct()
            .ToArray();
        if (retained.Any(row => row.Conflict) || sourceUuids.Length > 1 || artistUuids.Length > 1)
        {
            return UnresolvedSource(
                NormalizationCatalogBindingStatus.ProviderConflict,
                "retained_assignment_conflict"
            );
        }

        var source = matches.SingleOrDefault();
        if (sourceUuids.Length == 1)
        {
            if (source != null && source.SourceUuid != sourceUuids[0])
            {
                return UnresolvedSource(
                    NormalizationCatalogBindingStatus.ProviderConflict,
                    "retained_recording_changed"
                );
            }
            var accepted = await ReadSourceAsync(c, t, sourceUuids[0], ct);
            if (accepted == null)
            {
                return UnresolvedSource(
                    NormalizationCatalogBindingStatus.Missing,
                    "missing_retained_recording"
                );
            }
            if (accepted.UpstreamIdentifier != resource.ProviderKey || source == null)
            {
                return UnresolvedSource(
                    NormalizationCatalogBindingStatus.ProviderConflict,
                    "retained_provider_identity_changed"
                );
            }
        }
        if (source != null && artistUuids.Length == 1 && source.ArtistUuid != artistUuids[0])
        {
            return UnresolvedSource(
                NormalizationCatalogBindingStatus.ProviderConflict,
                "retained_artist_changed"
            );
        }
        return ResolveSource(
            source,
            expectedArtist,
            sourceUuids.Length == 1 ? "retained_archive_assignment" : "qualified_archive_item"
        );
    }

    private static NormalizationCatalogBinding<NormalizationCatalogFileReference> BindArchiveFile(
        ProviderFileObservation file,
        NormalizationResourceKey resource,
        SourceRow source,
        FileRow[] rows,
        HashSet<string> duplicateKeys
    )
    {
        var filename = file.Filename?.Value;
        if (string.IsNullOrWhiteSpace(filename))
        {
            return UnresolvedFile(
                NormalizationCatalogBindingStatus.Missing,
                "missing_file_identity"
            );
        }
        if (duplicateKeys.Contains(filename))
        {
            return UnresolvedFile(
                NormalizationCatalogBindingStatus.Ambiguous,
                "duplicate_file_identity"
            );
        }

        var identities = file
            .Identities.Where(identity =>
                identity.Identity.Provider == "archive.org"
                && identity.Identity.ResourceType == "item_file"
            )
            .Select(identity => identity.Identity.ProviderKey)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (
            identities.Length != 1
            || !IsArchiveFileIdentity(identities[0], resource.ProviderKey, filename)
        )
        {
            return UnresolvedFile(
                NormalizationCatalogBindingStatus.ProviderConflict,
                "file_identity_mismatch"
            );
        }

        // This is the existing Archive writer's literal media locator, not a UUID formula,
        // inferred redirect or title match. Encoding or redirect aliases require explicit evidence.
        var url = $"https://archive.org/download/{resource.ProviderKey}/{filename}";
        return BindExactMedia(source, rows, url);
    }

    private static bool IsArchiveFileIdentity(string key, string item, string filename)
    {
        try
        {
            var parts = JsonSerializer.Deserialize<string[]>(key);
            return parts is { Length: 2 } && parts[0] == item && parts[1] == filename;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed record ArchiveReference(Guid? SourceUuid, Guid? ArtistUuid, bool Conflict);
}
