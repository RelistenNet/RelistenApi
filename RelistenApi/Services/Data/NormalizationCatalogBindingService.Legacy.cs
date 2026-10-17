using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationCatalogBindingService
{
    private static async Task<SourceResolution> ResolveLegacyRecordingAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        NormalizationResourceKey resource,
        Guid? expectedArtist,
        CancellationToken ct
    )
    {
        if (!Guid.TryParse(resource.ProviderKey, out var uuid) || uuid == Guid.Empty)
        {
            throw new ArgumentException(
                "Legacy recording binding requires its stored source UUID."
            );
        }
        var source = await ReadSourceAsync(c, t, uuid, ct);
        return ResolveSource(source, expectedArtist, "exact_catalog_uuid");
    }

    private static Task<SourceRow?> ReadSourceAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid uuid,
        CancellationToken ct
    ) =>
        c.QuerySingleOrDefaultAsync<SourceRow>(
            new CommandDefinition(
                $"SELECT {SourceColumns} {SourceJoins} WHERE s.uuid=@uuid",
                new { uuid },
                t,
                cancellationToken: ct
            )
        );

    private static NormalizationCatalogBinding<NormalizationCatalogFileReference> BindFile(
        ProviderFileObservation file,
        NormalizationResourceKey resource,
        SourceRow source,
        FileRow[] rows,
        HashSet<string> duplicateKeys
    ) =>
        resource.Provider switch
        {
            "legacy_catalog" => BindLegacyFile(file, source, rows, duplicateKeys),
            "archive.org" => BindArchiveFile(file, resource, source, rows, duplicateKeys),
            "phish.in" => BindPhishFile(file, resource, source, rows, duplicateKeys),
            _ => throw new NotSupportedException(
                "Catalog file binding does not support this provider."
            ),
        };

    private static NormalizationCatalogBinding<NormalizationCatalogFileReference> BindLegacyFile(
        ProviderFileObservation file,
        SourceRow source,
        FileRow[] rows,
        HashSet<string> duplicateKeys
    )
    {
        var identities = file
            .Identities.Where(identity =>
                identity.Identity.Provider == "legacy_catalog"
                && identity.Identity.ResourceType == "track"
            )
            .Select(identity => identity.Identity.ProviderKey)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (identities.Length == 0)
        {
            return UnresolvedFile(
                NormalizationCatalogBindingStatus.Missing,
                "missing_file_identity"
            );
        }
        if (
            identities.Length != 1
            || !Guid.TryParse(identities[0], out var trackUuid)
            || trackUuid == Guid.Empty
        )
        {
            return UnresolvedFile(
                NormalizationCatalogBindingStatus.ProviderConflict,
                "invalid_file_identity"
            );
        }
        if (duplicateKeys.Contains(identities[0]))
        {
            return UnresolvedFile(
                NormalizationCatalogBindingStatus.Ambiguous,
                "duplicate_file_identity"
            );
        }

        var track = rows.SingleOrDefault(row => row.TrackUuid == trackUuid);
        if (track == null)
        {
            return UnresolvedFile(NormalizationCatalogBindingStatus.Missing, "missing_file");
        }
        var ownership = FileOwnershipIssue(source, track);
        if (ownership != null)
        {
            return ownership;
        }
        if (file.PlaybackLocator?.Value != track.MediaUrl)
        {
            return UnresolvedFile(
                NormalizationCatalogBindingStatus.ProviderConflict,
                "media_url_changed"
            );
        }
        return BoundFile(source, track);
    }

    private static NormalizationCatalogBinding<NormalizationCatalogFileReference>? FileOwnershipIssue(
        SourceRow source,
        FileRow track
    )
    {
        if (
            track.SourceId != source.SourceId
            || track.ArtistId != source.ArtistId
            || track.SetSourceId is { } owner && owner != source.SourceId
        )
        {
            return UnresolvedFile(
                NormalizationCatalogBindingStatus.ProviderConflict,
                "file_ownership_conflict"
            );
        }
        if (track.SourceSetUuid == null)
        {
            return UnresolvedFile(NormalizationCatalogBindingStatus.Missing, "missing_source_set");
        }
        if (track.Orphaned)
        {
            return UnresolvedFile(NormalizationCatalogBindingStatus.Missing, "orphaned_file");
        }
        if (string.IsNullOrWhiteSpace(track.MediaUrl))
        {
            return UnresolvedFile(NormalizationCatalogBindingStatus.Missing, "missing_media_url");
        }
        return null;
    }
}
