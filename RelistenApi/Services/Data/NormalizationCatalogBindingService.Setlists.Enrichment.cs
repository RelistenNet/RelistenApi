using System;
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
    private static async Task<NormalizationSetlistBindingSnapshot> BindSetlistFmSnapshotAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        NormalizationInputRevision input,
        ProviderSetlistObservation account,
        NormalizationMappingRegistryDependency registry,
        Guid? expectedArtistUuid,
        CancellationToken ct
    )
    {
        var pageSeparator = input.Resource.ProviderKey.LastIndexOf(
            ":page=",
            StringComparison.Ordinal
        );
        var artistKey = input.Resource.ProviderKey[..pageSeparator];
        var canonicalArtistKey = Guid.TryParseExact(artistKey, "D", out var mbid)
            ? mbid.ToString("D")
            : null;
        var performerKeys = account
            .Performers.SelectMany(performer => performer.Identities)
            .Where(identity =>
                identity.Identity.Provider == "musicbrainz.org"
                && identity.Identity.ResourceType == "artist"
            )
            .Select(identity => identity.Identity.ProviderKey)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var artists = (
            await c.QueryAsync<SetlistArtistRow>(
                new CommandDefinition(
                    """
                    SELECT DISTINCT a.id AS ArtistId,a.uuid AS ArtistUuid
                    FROM artists a JOIN artists_upstream_sources mapping ON mapping.artist_id=a.id
                    JOIN upstream_sources provider ON provider.id=mapping.upstream_source_id
                    WHERE provider.name='setlist.fm' AND (a.musicbrainz_id=@artistKey
                        OR (@canonicalArtistKey IS NOT NULL AND lower(a.musicbrainz_id)=@canonicalArtistKey))
                    ORDER BY a.uuid LIMIT @limit
                    """,
                    new
                    {
                        artistKey,
                        canonicalArtistKey,
                        limit = MaximumSetlistArtists + 1,
                    },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        if (performerKeys.Length != 1 || !MusicBrainzKeysEqual(performerKeys[0], artistKey))
        {
            return await ConflictingSetlistArtistAsync(
                c,
                t,
                input,
                account,
                artists,
                registry,
                "performer_resource_conflict",
                ct
            );
        }
        return await BindImportedSetlistSnapshotAsync(
            c,
            t,
            input,
            account,
            artists,
            account.Origin.Item.ProviderKey,
            registry,
            expectedArtistUuid,
            ct
        );
    }

    private static async Task<NormalizationSetlistBindingSnapshot> BindPhantasyTourSetlistSnapshotAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        NormalizationInputRevision input,
        ProviderSetlistObservation account,
        NormalizationMappingRegistryDependency registry,
        Guid? expectedArtistUuid,
        CancellationToken ct
    )
    {
        var performerKeys = account
            .Performers.SelectMany(performer => performer.Identities)
            .Where(identity =>
                identity.Identity.Provider == "phantasytour.com"
                && identity.Identity.ResourceType == "artist"
            )
            .Select(identity => identity.Identity.ProviderKey)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var artistKey = performerKeys.Length == 1 ? PositiveBandKey(performerKeys[0]) : null;
        var artists = (
            await c.QueryAsync<SetlistArtistRow>(
                new CommandDefinition(
                    """
                    SELECT DISTINCT a.id AS ArtistId,a.uuid AS ArtistUuid
                    FROM artists a JOIN artists_upstream_sources mapping ON mapping.artist_id=a.id
                    JOIN upstream_sources provider ON provider.id=mapping.upstream_source_id
                    WHERE provider.name='phantasytour.com'
                        -- The importer parses this configured key as uint before building requests.
                        AND mapping.upstream_identifier ~ '^[[:space:]]*\+?[0-9]+[[:space:]]*$'
                        AND ltrim(regexp_replace(mapping.upstream_identifier,'^[[:space:]]+|[[:space:]]+$','','g'),'+0')=@artistKey
                    ORDER BY a.uuid LIMIT @limit
                    """,
                    new { artistKey, limit = MaximumSetlistArtists + 1 },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        var pageSeparator = input.Resource.ProviderKey.IndexOf(":page=", StringComparison.Ordinal);
        var resourceConflict =
            input.Resource.ResourceType == "show-list"
            && (
                pageSeparator < 0
                || PositiveBandKey(input.Resource.ProviderKey[..pageSeparator]) != artistKey
            );
        if (
            artistKey == null
            || resourceConflict
            || !int.TryParse(
                account.Origin.Item.ProviderKey,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var showId
            )
            || showId <= 0
        )
        {
            return await ConflictingSetlistArtistAsync(
                c,
                t,
                input,
                account,
                artists,
                registry,
                "performer_resource_conflict",
                ct
            );
        }
        return await BindImportedSetlistSnapshotAsync(
            c,
            t,
            input,
            account,
            artists,
            "pt:" + showId.ToString(CultureInfo.InvariantCulture),
            registry,
            expectedArtistUuid,
            ct
        );
    }

    private static bool MusicBrainzKeysEqual(string left, string right) =>
        Guid.TryParseExact(left, "D", out var leftUuid)
        && Guid.TryParseExact(right, "D", out var rightUuid)
            ? leftUuid == rightUuid
            : left == right;

    private static string? PositiveBandKey(string value) =>
        uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
        && id > 0
            ? id.ToString(CultureInfo.InvariantCulture)
            : null;

    private static async Task<NormalizationSetlistBindingSnapshot> ConflictingSetlistArtistAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        NormalizationInputRevision input,
        ProviderSetlistObservation account,
        SetlistArtistRow[] artists,
        NormalizationMappingRegistryDependency registry,
        string reason,
        CancellationToken ct
    )
    {
        var clocks = await ReadSetlistArtistClocksAsync(
            c,
            t,
            artists.Select(artist => artist.ArtistUuid),
            ct
        );
        return new(
            input.Uuid,
            input.Resource,
            account.Origin,
            new(NormalizationCatalogBindingStatus.ProviderConflict, null, reason),
            new(NormalizationCatalogBindingStatus.ProviderConflict, null, "artist_unresolved"),
            new(NormalizationCatalogBindingStatus.ProviderConflict, null, "artist_unresolved"),
            clocks,
            registry
        );
    }
}
