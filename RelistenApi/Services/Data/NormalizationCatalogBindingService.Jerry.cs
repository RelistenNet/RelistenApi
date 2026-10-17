using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationCatalogBindingService
{
    private static async Task<NormalizationSetlistBindingSnapshot> BindJerrySetlistSnapshotAsync(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        NormalizationInputRevision input,
        ProviderSetlistObservation account,
        NormalizationMappingRegistryDependency registry,
        Guid? expectedArtistUuid,
        CancellationToken ct
    )
    {
        // The mirror key retains its configured prefix and the filename's basename without
        // .html. The catalog writer stores only that basename as the imported setlist key.
        // Dates, performer text and song links cannot establish this account association.
        var key = account.Origin.Item.ProviderKey;
        var separator = key.LastIndexOf('/');
        if (separator <= 0 || separator == key.Length - 1)
        {
            throw new ArgumentException(
                "Jerry mirror account requires its prefix and show identity."
            );
        }
        var prefix = key[..separator];
        var identifier = key[(separator + 1)..];
        var artists = (
            await c.QueryAsync<SetlistArtistRow>(
                new CommandDefinition(
                    """
                    SELECT DISTINCT a.id AS ArtistId,a.uuid AS ArtistUuid
                    FROM artists_upstream_sources mapping
                    JOIN upstream_sources provider ON provider.id=mapping.upstream_source_id
                    JOIN artists a ON a.id=mapping.artist_id
                    WHERE provider.name='jerrygarcia.com' AND mapping.upstream_identifier=@prefix
                    ORDER BY a.uuid LIMIT @limit
                    """,
                    new { prefix, limit = MaximumSetlistArtists + 1 },
                    t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        return await BindImportedSetlistSnapshotAsync(
            c,
            t,
            input,
            account,
            artists,
            identifier,
            registry,
            expectedArtistUuid,
            ct
        );
    }
}
