using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationCatalogBindingService
{
    private static async Task<SourceResolution> ResolvePhishRecordingAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        NormalizationResourceKey resource,
        Guid? expectedArtist,
        CancellationToken ct
    )
    {
        if (!IsPhishNumericIdentity(resource.ProviderKey))
        {
            throw new ArgumentException(
                "Phish.in recording binding requires its numeric show identity."
            );
        }

        // A configured artist does not own every numeric source key. Qualify the existing source
        // through its recording link, then detect global collisions before the expected-artist check.
        var matches = (
            await c.QueryAsync<SourceRow>(
                new CommandDefinition(
                    $"""
                    SELECT {SourceColumns} {SourceJoins}
                    WHERE s.upstream_identifier=@item AND EXISTS (
                        SELECT 1 FROM links l JOIN upstream_sources p ON p.id=l.upstream_source_id
                        WHERE l.source_id=s.id AND l.for_source AND p.name='phish.in')
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

        return ResolveSource(matches.SingleOrDefault(), expectedArtist, "qualified_phish_show");
    }

    private static NormalizationCatalogBinding<NormalizationCatalogFileReference> BindPhishFile(
        ProviderFileObservation file,
        NormalizationResourceKey resource,
        SourceRow source,
        FileRow[] rows,
        HashSet<string> duplicateKeys
    )
    {
        if (file.RecordingIdentity is { } owner && owner.Identity != resource)
        {
            return UnresolvedFile(
                NormalizationCatalogBindingStatus.ProviderConflict,
                "file_recording_identity_mismatch"
            );
        }

        var identities = file
            .Identities.Where(identity =>
                identity.Identity.Provider == "phish.in"
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
        if (identities.Length != 1 || !IsPhishNumericIdentity(identities[0]))
        {
            return UnresolvedFile(
                NormalizationCatalogBindingStatus.ProviderConflict,
                "invalid_file_identity"
            );
        }
        if (duplicateKeys.Contains("track:" + identities[0]))
        {
            return UnresolvedFile(
                NormalizationCatalogBindingStatus.Ambiguous,
                "duplicate_file_identity"
            );
        }

        var locator = file.PlaybackLocator?.Value;
        if (string.IsNullOrWhiteSpace(locator))
        {
            return UnresolvedFile(NormalizationCatalogBindingStatus.Missing, "missing_media_url");
        }

        // Match the writer's literal transformation. URI canonicalization, a title/position match
        // or the track ID suffix in an old slug cannot establish the current file association.
        var url = locator.Replace("http:", "https:");
        if (duplicateKeys.Contains("media:" + url))
        {
            return UnresolvedFile(
                NormalizationCatalogBindingStatus.Ambiguous,
                "duplicate_media_identity"
            );
        }
        return BindExactMedia(source, rows, url);
    }

    private static IReadOnlyList<string> PhishFileKeys(ProviderFileObservation file)
    {
        var keys = file
            .Identities.Where(identity =>
                identity.Identity.Provider == "phish.in"
                && identity.Identity.ResourceType == "track"
            )
            .Select(identity => "track:" + identity.Identity.ProviderKey)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (!string.IsNullOrWhiteSpace(file.PlaybackLocator?.Value))
        {
            keys.Add("media:" + file.PlaybackLocator.Value.Replace("http:", "https:"));
        }

        return keys;
    }

    private static bool IsPhishNumericIdentity(string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
        && id > 0
        && id.ToString(CultureInfo.InvariantCulture) == value;
}
