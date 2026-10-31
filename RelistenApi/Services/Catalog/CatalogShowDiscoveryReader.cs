using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;

namespace Relisten.Catalog;

/// <summary>
/// Reads filtered show summaries with the same metadata as artist/year browsing.
/// SQL date windows use the database session date; normalization never changes
/// the imported timestamps that determine recent membership or ranking.
/// </summary>
public sealed class CatalogShowDiscoveryReader(DbService db, CatalogShowMetadataReader metadata)
{
    public Task<IReadOnlyList<CatalogShow>?> ReadAnniversary(
        Guid? artistUuid,
        int? month,
        int? day,
        CancellationToken cancellationToken
    ) =>
        ReadSummaries(
            artistUuid,
            """
            EXTRACT(month FROM s.date)=COALESCE(@month,EXTRACT(month FROM CURRENT_DATE))
            AND EXTRACT(day FROM s.date)=COALESCE(@day,EXTRACT(day FROM CURRENT_DATE))
            """,
            "s.display_date,s.uuid",
            new { month, day },
            null,
            cancellationToken
        );

    /// <summary>
    /// Without a day window return 25 shows by default (at most 250). With days,
    /// return every match in that window, capped at 90 days, ignoring shows.
    /// Updated means shows.updated_at, matching the imported recording contract.
    /// </summary>
    public Task<IReadOnlyList<CatalogShow>?> ReadRecent(
        Guid? artistUuid,
        Guid[] artistUuids,
        bool performed,
        int? shows,
        int? days,
        CancellationToken cancellationToken
    )
    {
        var column = performed ? "s.date" : "s.updated_at";
        return ReadSummaries(
            artistUuid,
            "(@artistUuids::uuid[] IS NULL OR a.uuid=ANY(@artistUuids))"
                + (days.HasValue ? $" AND {column}>CURRENT_DATE-@days*INTERVAL '1 day'" : ""),
            performed ? "s.display_date DESC,s.uuid" : "s.updated_at DESC,s.uuid",
            new
            {
                artistUuids = artistUuids.Length == 0 ? null : artistUuids,
                days = days.HasValue ? Math.Min(days.Value, 90) : (int?)null,
            },
            days.HasValue ? null : Math.Min(shows ?? 25, 250),
            cancellationToken
        );
    }

    /// <summary>Orders by the newest source creation or update; the limit always applies.</summary>
    public Task<IReadOnlyList<CatalogShow>?> ReadRecentlyAdded(
        Guid? artistUuid,
        int limit,
        int? previousDays,
        bool orderByUpdated,
        CancellationToken cancellationToken
    )
    {
        var column = orderByUpdated ? "cnt.max_updated_at" : "cnt.max_created_at";
        // Saturate large windows at year 1, the earliest representable catalog timestamp.
        // Keep the timestamp column bare so PostgreSQL can use its existing index.
        return ReadSummaries(
            artistUuid,
            // A source timestamp is required: shows with no recording cannot be recently added.
            $"{column} IS NOT NULL"
                + (
                    previousDays.HasValue
                        ? $" AND {column}>=CURRENT_DATE-LEAST(@previousDays,CURRENT_DATE-DATE '0001-01-01')*INTERVAL '1 day'"
                        : ""
                ),
            $"{column} DESC,s.uuid",
            new { previousDays },
            Math.Min(limit, 200),
            cancellationToken
        );
    }

    /// <summary>Rating-weighted source rank, independent of listening popularity/trending.</summary>
    public Task<IReadOnlyList<CatalogShow>?> ReadTop(
        Guid artistUuid,
        int limit,
        CancellationToken cancellationToken
    ) =>
        ReadSummaries(
            artistUuid,
            "cnt.show_id IS NOT NULL",
            "cnt.max_avg_rating_weighted DESC NULLS LAST,s.uuid",
            null,
            limit,
            cancellationToken
        );

    private async Task<IReadOnlyList<CatalogShow>?> ReadSummaries(
        Guid? artistUuid,
        string predicate,
        string orderBy,
        object? parameters,
        int? limit,
        CancellationToken cancellationToken
    )
    {
        await using var session = await CatalogReadSession.Open(db, cancellationToken);
        if (
            artistUuid.HasValue
            && !await CatalogBrowseReader.ArtistExists(session, artistUuid.Value, cancellationToken)
        )
        {
            return null;
        }

        var query = new DynamicParameters(parameters);
        query.Add("artistUuid", artistUuid);
        query.Add("limit", limit);
        // Predicates and orderings above are fixed SQL; all caller values are parameters.
        var shows = (
            await session.Connection.QueryAsync<CatalogShow>(
                session.Command(
                    CatalogShowMetadataReader.ShowSelect
                        + " WHERE "
                        + (artistUuid.HasValue ? "a.uuid=@artistUuid AND " : "")
                        + predicate
                        + " ORDER BY "
                        + orderBy
                        + " LIMIT @limit",
                    query,
                    cancellationToken
                )
            )
        ).ToArray();
        await metadata.Apply(session, shows, cancellationToken);
        return shows;
    }
}
