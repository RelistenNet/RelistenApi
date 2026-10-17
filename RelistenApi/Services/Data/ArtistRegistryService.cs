using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Dapper;
using Relisten.Api.Models;

namespace Relisten.Data;

/// <summary>
/// Protects Taper's Section cohort identities from later artist and collection writes.
/// Call these checks while holding CatalogImportTransaction's artist-registry lock so
/// materialization cannot race the check. Direct administrative writes must do the same.
/// </summary>
public sealed class ArtistRegistryService(DbService db)
{
    /// <summary>Normalizes an exact identity label without fuzzy matching or word removal.</summary>
    public static string NormalizeIdentityKey(string label) =>
        Regex
            .Replace(label.Normalize(NormalizationForm.FormKC).Trim(), @"\s+", " ")
            .ToUpperInvariant();

    /// <summary>Rejects renames of cohort artists and collisions with another cohort identity.</summary>
    public async Task EnsureArtistWriteAllowedAsync(SlimArtist artist)
    {
        var cohort = await LoadCohortAsync();
        var ownMembership = cohort.SingleOrDefault(candidate => candidate.artist_id == artist.id);
        if (
            ownMembership != null
            && (
                artist.name != ownMembership.proposed_name
                || artist.slug != ownMembership.creation_slug
            )
        )
        {
            throw new ArtistIntakeIdentityConflictException(
                ownMembership.artist_uuid,
                "A Taper's Section artist must keep its creation name and slug."
            );
        }

        RejectCollision(cohort, artist.id, [artist.name, artist.slug]);
    }

    /// <summary>
    /// Rejects the shared Archive collection as an artist mapping and collection labels
    /// already reserved by another cohort artist. Dedicated collections may be attached
    /// to the cohort artist that owns the matching reviewed identity.
    /// </summary>
    public async Task EnsureMappingsAllowedAsync(
        int artistId,
        IEnumerable<SlimArtistUpstreamSource> sources
    )
    {
        var archiveSourceId = await db.WithWriteConnection(connection =>
            connection.QuerySingleAsync<int>(
                "SELECT id FROM upstream_sources WHERE name = 'archive.org'"
            )
        );
        var archiveLabels = sources
            .Where(source => source.upstream_source_id == archiveSourceId)
            .Select(source => source.upstream_identifier)
            .Where(label => label != null)
            .Cast<string>()
            .ToArray();
        var cohort = await LoadCohortAsync();
        if (archiveLabels.Any(label => NormalizeIdentityKey(label) == "TAPERSSECTION"))
        {
            var artistUuid = await db.WithWriteConnection(connection =>
                connection.QuerySingleOrDefaultAsync<Guid?>(
                    "SELECT uuid FROM artists WHERE id = @artistId",
                    new { artistId }
                )
            );
            throw new ArtistIntakeIdentityConflictException(
                artistUuid,
                "The shared taperssection collection cannot be assigned to an artist."
            );
        }

        RejectCollision(cohort, artistId, archiveLabels);
    }

    /// <summary>Collection discovery leaves a recognized cohort identity for explicit attachment.</summary>
    public async Task<bool> HasCohortIdentityConflictAsync(params string[] labels)
    {
        var keys = labels.Select(NormalizeIdentityKey).ToHashSet(StringComparer.Ordinal);
        return (await LoadCohortAsync()).Any(candidate => candidate.Keys.Any(keys.Contains));
    }

    private Task<IEnumerable<CohortIdentity>> LoadCohortAsync() =>
        db.WithWriteConnection(connection =>
            connection.QueryAsync<CohortIdentity>(
                """
                SELECT artist_id, artist_uuid, proposed_name, creation_slug, reviewed_creator_keys
                FROM normalization.archive_artist_candidates WHERE artist_uuid IS NOT NULL
                """
            )
        );

    private static void RejectCollision(
        IEnumerable<CohortIdentity> cohort,
        int artistId,
        IEnumerable<string> labels
    )
    {
        var keys = labels.Select(NormalizeIdentityKey).ToHashSet(StringComparer.Ordinal);
        var collision = cohort.FirstOrDefault(candidate =>
            candidate.artist_id != artistId && candidate.Keys.Any(keys.Contains)
        );
        if (collision != null)
        {
            throw new ArtistIntakeIdentityConflictException(
                collision.artist_uuid,
                "The requested identity is already assigned to a Taper's Section artist."
            );
        }
    }

    private sealed class CohortIdentity
    {
        public int artist_id { get; set; }
        public Guid artist_uuid { get; set; }
        public string proposed_name { get; set; } = "";
        public string creation_slug { get; set; } = "";
        public string[] reviewed_creator_keys { get; set; } = [];
        public IEnumerable<string> Keys =>
            reviewed_creator_keys
                .Select(NormalizeIdentityKey)
                .Concat([NormalizeIdentityKey(proposed_name), NormalizeIdentityKey(creation_slug)]);
    }
}

/// <summary>An artist edit would replace or duplicate a retained intake identity.</summary>
public sealed class ArtistIntakeIdentityConflictException(Guid? artistUuid, string message)
    : Exception(message)
{
    /// <summary>The catalog identity affected by the edit, or null before a new artist exists.</summary>
    public Guid? ArtistUuid { get; } = artistUuid;
}
