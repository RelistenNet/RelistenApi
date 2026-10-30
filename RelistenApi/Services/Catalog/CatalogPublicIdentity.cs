using System;
using System.Threading;
using System.Threading.Tasks;
using Dapper;

namespace Relisten.Catalog;

/// <summary>Distinguishes a retired canonical identity from an unknown lookup.</summary>
internal static class CatalogPublicIdentity
{
    public static async Task RequireNotRetired(
        CatalogReadSession session,
        string id,
        CancellationToken cancellationToken
    )
    {
        var retired = await session.Connection.QuerySingleAsync<bool>(
            session.Command(
                "SELECT EXISTS(SELECT 1 FROM norm.public_ids WHERE id=@id AND retired_at IS NOT NULL)",
                new { id },
                cancellationToken
            )
        );
        if (retired)
        {
            throw new CatalogRetiredResourceException();
        }
    }
}

internal sealed class CatalogRetiredResourceException : Exception;
