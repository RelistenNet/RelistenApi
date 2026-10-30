using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;

namespace Relisten.Catalog;

/// <summary>Reads a source's complete imported reviews without loading its playback hierarchy.</summary>
public sealed class CatalogSourceReviewReader(DbService db)
{
    /// <summary>Null means an unknown source; an empty list is an authoritative empty collection.</summary>
    public async Task<IReadOnlyList<CatalogSourceReview>?> ReadReviews(
        Guid sourceUuid,
        CancellationToken cancellationToken = default
    )
    {
        await using var session = await CatalogReadSession.Open(db, cancellationToken);
        var sourceId = await session.Connection.QuerySingleOrDefaultAsync<long?>(
            session.Command(
                "SELECT id FROM sources WHERE uuid=@sourceUuid",
                new { sourceUuid },
                cancellationToken
            )
        );
        if (sourceId is null)
        {
            return null;
        }

        return (
            await session.Connection.QueryAsync<CatalogSourceReview>(
                session.Command(
                    """
                    SELECT r.uuid,@sourceUuid::uuid source_uuid,r.rating,r.title,r.review,r.author,
                           r.created_at,r.updated_at
                    FROM source_reviews r WHERE r.source_id=@sourceId
                    ORDER BY r.updated_at DESC,r.uuid
                    """,
                    new { sourceUuid, sourceId },
                    cancellationToken
                )
            )
        ).ToArray();
    }
}

public sealed class CatalogSourceReview : CatalogTimestamps
{
    public Guid uuid { get; set; }
    public Guid source_uuid { get; set; }
    public int? rating { get; set; }
    public string? title { get; set; }
    public string review { get; set; } = null!;
    public string? author { get; set; }
}
