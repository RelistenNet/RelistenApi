using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Relisten.Catalog;

/// <summary>REST discovery composes complete or bounded views from the same membership
/// selector and entity hydration as GraphQL. Windows use the database session date.</summary>
public sealed class CatalogShowDiscoveryReader(
    DbService db,
    CatalogCollectionReader collections,
    CatalogEntityReader entities
)
{
    public Task<IReadOnlyList<CatalogShow>?> ReadAnniversary(
        Guid? artistUuid,
        int? month,
        int? day,
        CancellationToken cancellationToken
    ) =>
        Read(
            new(Artist: artistUuid, Month: month ?? 0, Day: day ?? 0),
            CatalogSort.Default,
            null,
            cancellationToken
        );

    /// <summary>Without days, return 25 shows by default, at most 250. A day window
    /// returns every match and is capped at 90 days; updated uses shows.updated_at.</summary>
    public Task<IReadOnlyList<CatalogShow>?> ReadRecent(
        Guid? artistUuid,
        Guid[] artistUuids,
        bool performed,
        int? shows,
        int? days,
        CancellationToken cancellationToken
    ) =>
        Read(
            new(
                Artist: artistUuid,
                Artists: artistUuids.Length == 0 ? null : artistUuids,
                Recency: performed ? CatalogRecency.Performed : CatalogRecency.Updated,
                RecentDays: days
            ),
            performed ? CatalogSort.DateDescending : CatalogSort.Updated,
            days.HasValue ? null : Math.Min(shows ?? 25, 250),
            cancellationToken
        );

    /// <summary>Orders by the newest source creation or update; the limit always applies.</summary>
    public Task<IReadOnlyList<CatalogShow>?> ReadRecentlyAdded(
        Guid? artistUuid,
        int limit,
        int? previousDays,
        bool orderByUpdated,
        CancellationToken cancellationToken
    ) =>
        Read(
            new(Artist: artistUuid, Recency: CatalogRecency.Added, RecentDays: previousDays),
            orderByUpdated ? CatalogSort.Updated : CatalogSort.Added,
            Math.Min(limit, 200),
            cancellationToken
        );

    /// <summary>Rating-weighted source rank, independent of listening popularity.</summary>
    public Task<IReadOnlyList<CatalogShow>?> ReadTop(
        Guid artistUuid,
        int limit,
        CancellationToken cancellationToken
    ) =>
        Read(
            new(Artist: artistUuid, RequireSources: true),
            CatalogSort.Rating,
            limit,
            cancellationToken
        );

    private async Task<IReadOnlyList<CatalogShow>?> Read(
        CatalogFilters filters,
        CatalogSort sort,
        int? limit,
        CancellationToken token
    )
    {
        await using var session = await CatalogReadSession.Open(db, token);
        if (
            filters.Artist is { } artist
            && !await CatalogBrowseReader.ArtistExists(session, artist, token)
        )
        {
            return null;
        }
        var collection = new CatalogCollection(
            CatalogReferenceKind.Show,
            Filters: filters,
            Sort: sort
        );
        var selection = limit is { } first
            ? (await collections.ReadPages(session, [(collection, new(first))], token))[0]
            : await collections.ReadAll(session, collection, token);
        var hydrated = await entities.Read(session, selection.Targets, token);
        var shows = hydrated.Shows.ToDictionary(s => s.uuid);
        return selection.Targets.Select(t => shows[t.Uuid!.Value]).ToArray();
    }
}
