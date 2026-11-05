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
    IReadOnlyList<CatalogShow> PopularShows,
    IReadOnlyList<CatalogShow> TrendingShows
);

/// <summary>
/// Selects stored ranking rows before hydrating current catalog resources. Scores
/// and metadata share the read snapshot; GETs never calculate or refresh scores.
/// Rankings remain transient online results for mobile.
/// </summary>
public sealed class CatalogRankingReader(
    DbService db,
    CatalogBrowseReader browse,
    CatalogShowMetadataReader metadata,
    PopularityRefreshRequest refresh
)
{
    public async Task<IReadOnlyList<CatalogRanked<CatalogArtist>>> ReadArtists(
        bool trending,
        int limit,
        CancellationToken cancellationToken
    )
    {
        await using var session = await CatalogReadSession.Open(db, cancellationToken);
        var scores = await ReadGlobalScores(
            session,
            "artist",
            trending,
            limit,
            PopularitySortWindow.Days30,
            cancellationToken
        );
        var artists = (
            await browse.ReadArtists(
                session,
                scores.Select(row => row.uuid).ToArray(),
                cancellationToken
            )
        ).ToDictionary(artist => artist.uuid);
        return scores
            .Where(row => artists.ContainsKey(row.uuid))
            .Select(row => new CatalogRanked<CatalogArtist>(artists[row.uuid], row.ToMetrics()!))
            .ToArray();
    }

    public async Task<IReadOnlyList<CatalogRanked<CatalogYear>>> ReadYears(
        bool trending,
        int limit,
        CancellationToken cancellationToken
    )
    {
        await using var session = await CatalogReadSession.Open(db, cancellationToken);
        var scores = await ReadGlobalScores(
            session,
            "year",
            trending,
            limit,
            PopularitySortWindow.Days30,
            cancellationToken
        );
        var years = (
            await session.Connection.QueryAsync<CatalogYear>(
                session.Command(
                    CatalogBrowseReader.YearSelect + " WHERE y.uuid=ANY(@ids)",
                    new { ids = scores.Select(row => row.uuid).ToArray() },
                    cancellationToken
                )
            )
        ).ToDictionary(year => year.uuid);
        return scores
            .Where(row => years.ContainsKey(row.uuid))
            .Select(row => new CatalogRanked<CatalogYear>(years[row.uuid], row.ToMetrics()!))
            .ToArray();
    }

    public async Task<IReadOnlyList<CatalogShow>> ReadShows(
        bool trending,
        int limit,
        PopularitySortWindow window,
        CancellationToken cancellationToken
    )
    {
        await using var session = await CatalogReadSession.Open(db, cancellationToken);
        var scores = await ReadGlobalScores(
            session,
            "show",
            trending,
            limit,
            window,
            cancellationToken
        );
        var shows = await ReadShows(
            session,
            scores.Select(row => row.uuid).ToArray(),
            cancellationToken
        );
        var map = scores.ToDictionary(row => row.uuid, row => row.ToMetrics()!);
        await metadata.Apply(session, shows, cancellationToken, map);
        var byId = shows.ToDictionary(show => show.uuid);
        return scores
            .Where(row => byId.ContainsKey(row.uuid))
            .Select(row => byId[row.uuid])
            .ToArray();
    }

    /// <summary>Unknown filter artists are omitted; existing artists retain empty groups.</summary>
    public async Task<IReadOnlyList<CatalogArtistRankings>> ReadArtistGroups(
        Guid[] artists,
        int limit,
        PopularitySortWindow window,
        CancellationToken cancellationToken
    )
    {
        if (artists.Length == 0)
        {
            return [];
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
        foreach (var artist in owners)
        {
            refresh.Require(PopularityPopulationKind.Show, artist);
        }
        var popularOrder = ShowOrder(false, window);
        var trendingOrder = ShowOrder(true, window);
        // Apply each artist's limit before loading show metadata. The ordinal
        // preserves SQL ordering across the two selections and the outer join.
        var selected = (
            await session.Connection.QueryAsync<ArtistSelection>(
                session.Command(
                    $"""
                    SELECT ranked.* FROM unnest(@artists::uuid[]) owner(uuid)
                    CROSS JOIN LATERAL (
                        (SELECT uuid,artist_uuid,false trending,row_number() OVER (ORDER BY {popularOrder}) ordinal
                         FROM show_popularity WHERE NOT is_global AND artist_uuid=owner.uuid AND momentum_score IS NOT NULL
                         ORDER BY {popularOrder} LIMIT @limit)
                        UNION ALL
                        (SELECT uuid,artist_uuid,true trending,row_number() OVER (ORDER BY {trendingOrder}) ordinal
                         FROM show_popularity WHERE NOT is_global AND artist_uuid=owner.uuid AND momentum_score IS NOT NULL
                           AND plays_48h>=@recentFloor AND plays_30d>=@monthlyFloor
                         ORDER BY {trendingOrder} LIMIT @limit)
                    ) ranked ORDER BY artist_uuid,trending,ordinal
                    """,
                    new
                    {
                        artists = owners,
                        limit,
                        recentFloor = PopularityService.ShowTrendingPlays48hFloor,
                        monthlyFloor = PopularityService.ShowTrendingPlays30dFloor,
                    },
                    cancellationToken
                )
            )
        ).ToArray();
        var shows = await ReadShows(
            session,
            selected.Select(row => row.uuid).Distinct().ToArray(),
            cancellationToken
        );
        await metadata.Apply(session, shows, cancellationToken);
        var byId = shows.ToDictionary(show => show.uuid);
        IReadOnlyList<CatalogShow> Group(Guid artist, bool trending) =>
            selected
                .Where(row =>
                    row.artist_uuid == artist
                    && row.trending == trending
                    && byId.TryGetValue(row.uuid, out var show)
                    && show.artist_uuid == artist
                )
                .Select(row => byId[row.uuid])
                .ToArray();
        return owners
            .Select(artist => new CatalogArtistRankings(
                artist,
                Group(artist, false),
                Group(artist, true)
            ))
            .ToArray();
    }

    public async Task<IReadOnlyList<CatalogShow>> ReadMomentum(
        Guid[] artists,
        int limit,
        CancellationToken cancellationToken
    )
    {
        if (artists.Length == 0)
        {
            return [];
        }
        await using var session = await CatalogReadSession.Open(db, cancellationToken);
        foreach (var artist in artists.Distinct())
        {
            refresh.Require(PopularityPopulationKind.Show, artist);
        }
        var selected = (
            await session.Connection.QueryAsync<StoredPopularity>(
                session.Command(
                    """
                    SELECT * FROM show_popularity WHERE NOT is_global AND artist_uuid=ANY(@artists) AND momentum_score IS NOT NULL
                    ORDER BY COALESCE(organic_momentum_score,momentum_score) DESC,momentum_score DESC,
                             hot_score_30d DESC,plays_30d DESC,uuid
                    LIMIT @limit
                    """,
                    new { artists, limit },
                    cancellationToken
                )
            )
        ).ToArray();
        var shows = (
            await ReadShows(session, selected.Select(row => row.uuid).ToArray(), cancellationToken)
        ).ToDictionary(show => show.uuid);
        var owned = selected
            .Where(row =>
                shows.TryGetValue(row.uuid, out var show) && show.artist_uuid == row.artist_uuid
            )
            .ToArray();
        var ordered = owned.Select(row => shows[row.uuid]).ToArray();
        await metadata.Apply(
            session,
            ordered,
            cancellationToken,
            owned.ToDictionary(row => row.uuid, row => row.ToMetrics(organic: true)!)
        );
        return ordered;
    }

    private async Task<StoredPopularity[]> ReadGlobalScores(
        CatalogReadSession session,
        string kind,
        bool trending,
        int limit,
        PopularitySortWindow window,
        CancellationToken cancellationToken
    )
    {
        var (recentFloor, monthlyFloor, populationKind) = kind switch
        {
            "artist" => (
                PopularityService.ArtistTrendingPlays48hFloor,
                PopularityService.ArtistTrendingPlays30dFloor,
                PopularityPopulationKind.Artist
            ),
            "year" => (
                PopularityService.YearTrendingPlays48hFloor,
                PopularityService.YearTrendingPlays30dFloor,
                PopularityPopulationKind.Year
            ),
            "show" => (
                PopularityService.ShowTrendingPlays48hFloor,
                PopularityService.ShowTrendingPlays30dFloor,
                PopularityPopulationKind.Show
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        refresh.Require(populationKind);
        var population = kind == "artist" ? "plays_30d>0" : "is_global";
        var threshold = trending ? " AND plays_48h>=@recentFloor AND plays_30d>=@monthlyFloor" : "";
        var order =
            kind == "show" ? ShowOrder(trending, window)
            : trending ? "trend_ratio DESC,hot_score_30d DESC,uuid"
            : "hot_score_30d DESC,plays_30d DESC,uuid";
        return (
            await session.Connection.QueryAsync<StoredPopularity>(
                session.Command(
                    $"SELECT * FROM {kind}_popularity WHERE {population} AND momentum_score IS NOT NULL{threshold} ORDER BY {order} LIMIT @limit",
                    new
                    {
                        recentFloor,
                        monthlyFloor,
                        limit,
                    },
                    cancellationToken
                )
            )
        ).ToArray();
    }

    private static string ShowOrder(bool trending, PopularitySortWindow window)
    {
        var suffix = window switch
        {
            PopularitySortWindow.Hours48 => "48h",
            PopularitySortWindow.Days7 => "7d",
            _ => "30d",
        };
        return $"hot_score_{suffix} DESC,plays_{suffix} DESC,"
            + (trending ? "trend_ratio DESC," : "")
            + "uuid";
    }

    private static async Task<CatalogShow[]> ReadShows(
        CatalogReadSession session,
        Guid[] ids,
        CancellationToken cancellationToken
    )
    {
        if (ids.Length == 0)
        {
            return [];
        }
        return (
            await session.Connection.QueryAsync<CatalogShow>(
                session.Command(
                    CatalogShowMetadataReader.ShowSelect + " WHERE s.uuid=ANY(@ids)",
                    new { ids },
                    cancellationToken
                )
            )
        ).ToArray();
    }

    private sealed class ArtistSelection
    {
        public Guid uuid { get; set; }
        public Guid artist_uuid { get; set; }
        public bool trending { get; set; }
        public long ordinal { get; set; }
    }
}
