using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Relisten.Api.Models;
using Relisten.Services.Popularity;

namespace Relisten.Catalog;

public sealed record CatalogRanked<T>(T Resource, PopularityMetrics Metrics);

public sealed record CatalogArtistRankings(
    Guid ArtistUuid,
    IReadOnlyList<CatalogRanked<CatalogShow>> PopularShows,
    IReadOnlyList<CatalogRanked<CatalogShow>> TrendingShows
);

/// <summary>
/// Ranking maps are asynchronous inputs, loaded once before opening the catalog
/// snapshot. Hydrates current metadata here so cached ranks never freeze venues.
/// </summary>
public sealed class CatalogRankingReader(
    DbService db,
    PopularityService popularity,
    CatalogShowMetadataReader metadata
)
{
    public async Task<IReadOnlyList<CatalogRanked<CatalogArtist>>> ReadArtists(
        bool trending,
        int limit,
        CancellationToken cancellationToken
    )
    {
        var map = await popularity.GetArtistPopularityMap();
        var selected = Rank(
                map.Where(entry => entry.Value.windows.days_30d.plays > 0)
                    .Select(entry => new CatalogRanked<Guid>(entry.Key, entry.Value)),
                trending,
                PopularityService.ArtistTrendingPlays48hFloor,
                PopularityService.ArtistTrendingPlays30dFloor,
                PopularitySortWindow.Days30,
                id => id
            )
            .Take(limit)
            .ToArray();
        await using var session = await CatalogReadSession.Open(db, cancellationToken);
        var artists = (
            await CatalogBrowseReader.ReadArtists(
                session,
                selected.Select(item => item.Resource).ToArray(),
                cancellationToken
            )
        ).ToDictionary(artist => artist.uuid);
        return selected
            .Where(item => artists.ContainsKey(item.Resource))
            .Select(item => new CatalogRanked<CatalogArtist>(artists[item.Resource], item.Metrics))
            .ToArray();
    }

    public async Task<IReadOnlyList<CatalogRanked<CatalogYear>>> ReadYears(
        bool trending,
        int limit,
        CancellationToken cancellationToken
    )
    {
        var map = await popularity.GetGlobalYearPopularityMap();
        var selected = Rank(
                map.Select(entry => new CatalogRanked<Guid>(entry.Key, entry.Value)),
                trending,
                PopularityService.YearTrendingPlays48hFloor,
                PopularityService.YearTrendingPlays30dFloor,
                PopularitySortWindow.Days30,
                id => id
            )
            .Take(limit)
            .ToArray();
        await using var session = await CatalogReadSession.Open(db, cancellationToken);
        var years = (
            await session.Connection.QueryAsync<CatalogYear>(
                session.Command(
                    "SELECT y.*,a.uuid artist_uuid FROM years y JOIN artists a ON a.id=y.artist_id WHERE y.uuid=ANY(@ids)",
                    new { ids = selected.Select(item => item.Resource).ToArray() },
                    cancellationToken
                )
            )
        ).ToDictionary(year => year.uuid);
        return selected
            .Where(item => years.ContainsKey(item.Resource))
            .Select(item => new CatalogRanked<CatalogYear>(years[item.Resource], item.Metrics))
            .ToArray();
    }

    public async Task<IReadOnlyList<CatalogRanked<CatalogShow>>> ReadShows(
        bool trending,
        int limit,
        PopularitySortWindow window,
        CancellationToken cancellationToken
    )
    {
        var map = await popularity.GetGlobalShowPopularityMap();
        await using var session = await CatalogReadSession.Open(db, cancellationToken);
        var selected = Rank(
                map.Select(entry => new CatalogRanked<Guid>(entry.Key, entry.Value)),
                trending,
                PopularityService.ShowTrendingPlays48hFloor,
                PopularityService.ShowTrendingPlays30dFloor,
                window,
                id => id,
                trendByRatio: false
            )
            .Take(limit)
            .ToArray();
        var shows = (
            await ReadShows(
                session,
                selected.Select(item => item.Resource).ToArray(),
                cancellationToken
            )
        ).ToDictionary(show => show.uuid);
        await metadata.Apply(session, shows.Values.ToArray(), cancellationToken);
        return selected
            .Where(item => shows.ContainsKey(item.Resource))
            .Select(item => new CatalogRanked<CatalogShow>(shows[item.Resource], item.Metrics))
            .ToArray();
    }

    /// <summary>Empty selection stays empty. Unknown filter artists are omitted; a single unknown path is 404.</summary>
    public async Task<IReadOnlyList<CatalogArtistRankings>> ReadArtistGroups(
        Guid[] artists,
        int limit,
        PopularitySortWindow window,
        CancellationToken cancellationToken
    )
    {
        var maps = new Dictionary<Guid, Dictionary<Guid, PopularityMetrics>>();
        foreach (var artist in artists.Distinct().Order())
        {
            maps[artist] = await popularity.GetShowPopularityMapForArtist(artist);
        }
        await using var session = await CatalogReadSession.Open(db, cancellationToken);
        var owners = (
            await session.Connection.QueryAsync<Guid>(
                session.Command(
                    "SELECT uuid FROM artists WHERE uuid=ANY(@artists) ORDER BY uuid",
                    new { artists },
                    cancellationToken
                )
            )
        ).ToArray();
        var selectedIds = maps
            .Values.SelectMany(map =>
                Rank(
                        map.Select(entry => new CatalogRanked<Guid>(entry.Key, entry.Value)),
                        false,
                        PopularityService.ShowTrendingPlays48hFloor,
                        PopularityService.ShowTrendingPlays30dFloor,
                        window,
                        id => id,
                        trendByRatio: false
                    )
                    .Take(limit)
                    .Concat(
                        Rank(
                                map.Select(entry => new CatalogRanked<Guid>(
                                    entry.Key,
                                    entry.Value
                                )),
                                true,
                                PopularityService.ShowTrendingPlays48hFloor,
                                PopularityService.ShowTrendingPlays30dFloor,
                                window,
                                id => id,
                                trendByRatio: false
                            )
                            .Take(limit)
                    )
                    .Select(item => item.Resource)
            )
            .Distinct()
            .ToArray();
        var shows = await ReadShows(session, selectedIds, cancellationToken);
        await metadata.Apply(session, shows, cancellationToken);
        return owners
            .Select(artist =>
            {
                var owned = shows.Where(show => show.artist_uuid == artist).ToArray();
                return new CatalogArtistRankings(
                    artist,
                    RankShows(owned, maps[artist], false, window).Take(limit).ToArray(),
                    RankShows(owned, maps[artist], true, window).Take(limit).ToArray()
                );
            })
            .ToArray();
    }

    public async Task<IReadOnlyList<CatalogRanked<CatalogShow>>> ReadMomentum(
        Guid[] artists,
        int limit,
        CancellationToken cancellationToken
    )
    {
        var candidates = new Dictionary<Guid, (Guid Artist, CachedShowPopularity Cached)>();
        foreach (var artist in artists.Distinct().Order())
        {
            var map = await popularity.GetShowPopularityCacheMapForArtist(artist);
            foreach (var (id, cached) in map)
            {
                candidates.TryAdd(id, (artist, cached));
            }
        }
        await using var session = await CatalogReadSession.Open(db, cancellationToken);
        var selectedIds = candidates
            .OrderByDescending(entry =>
                CachedShowPopularityMapper.GetRankingMomentumScore(entry.Value.Cached)
            )
            .ThenByDescending(entry =>
                CachedShowPopularityMapper.GetRawMomentumScore(entry.Value.Cached)
            )
            .ThenByDescending(entry => entry.Value.Cached.windows.days_30d.hot_score)
            .ThenByDescending(entry => entry.Value.Cached.windows.days_30d.plays)
            .ThenBy(entry => entry.Key)
            .Take(limit)
            .Select(entry => entry.Key)
            .ToArray();
        var shows = (await ReadShows(session, selectedIds, cancellationToken)).ToDictionary(show =>
            show.uuid
        );
        await metadata.Apply(session, shows.Values.ToArray(), cancellationToken);
        return selectedIds
            .Where(id =>
                shows.TryGetValue(id, out var show) && show.artist_uuid == candidates[id].Artist
            )
            .Select(id =>
            {
                var cached = candidates[id].Cached;
                var metrics = CachedShowPopularityMapper.ToPopularityMetrics(cached);
                metrics.momentum_score = CachedShowPopularityMapper.GetRankingMomentumScore(cached);
                return new CatalogRanked<CatalogShow>(shows[id], metrics);
            })
            .ToArray();
    }

    private async Task<CatalogShow[]> ReadShows(
        CatalogReadSession session,
        Guid[] ids,
        CancellationToken cancellationToken
    )
    {
        if (ids.Length == 0)
        {
            return [];
        }
        var shows = (
            await session.Connection.QueryAsync<CatalogShow>(
                session.Command(
                    CatalogShowMetadataReader.ShowSelect + " WHERE s.uuid=ANY(@ids)",
                    new { ids },
                    cancellationToken
                )
            )
        ).ToArray();
        return shows;
    }

    private static IEnumerable<CatalogRanked<CatalogShow>> RankShows(
        IEnumerable<CatalogShow> shows,
        IReadOnlyDictionary<Guid, PopularityMetrics> map,
        bool trending,
        PopularitySortWindow window
    ) =>
        Rank(
            shows
                .Where(show => map.ContainsKey(show.uuid))
                .Select(show => new CatalogRanked<CatalogShow>(show, map[show.uuid])),
            trending,
            PopularityService.ShowTrendingPlays48hFloor,
            PopularityService.ShowTrendingPlays30dFloor,
            window,
            show => show.uuid,
            trendByRatio: false
        );

    private static IOrderedEnumerable<CatalogRanked<T>> Rank<T>(
        IEnumerable<CatalogRanked<T>> items,
        bool trending,
        int recentFloor,
        int monthlyFloor,
        PopularitySortWindow window,
        Func<T, Guid> id,
        bool trendByRatio = true
    )
    {
        if (trending)
        {
            items = items.Where(item =>
                item.Metrics.windows.hours_48h.plays >= recentFloor
                && item.Metrics.windows.days_30d.plays >= monthlyFloor
            );
        }
        PopularityWindowMetrics Window(PopularityMetrics metrics) =>
            window switch
            {
                PopularitySortWindow.Hours48 => metrics.windows.hours_48h,
                PopularitySortWindow.Days7 => metrics.windows.days_7d,
                _ => metrics.windows.days_30d,
            };
        var ranked =
            trending && trendByRatio
                ? items
                    .OrderByDescending(item => item.Metrics.trend_ratio)
                    .ThenByDescending(item => Window(item.Metrics).hot_score)
                : items
                    .OrderByDescending(item => Window(item.Metrics).hot_score)
                    .ThenByDescending(item => Window(item.Metrics).plays);
        if (trending && !trendByRatio)
        {
            ranked = ranked.ThenByDescending(item => item.Metrics.trend_ratio);
        }
        return ranked.ThenBy(item => id(item.Resource));
    }
}
