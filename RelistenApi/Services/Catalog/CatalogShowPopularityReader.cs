using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Relisten.Services.Popularity;

namespace Relisten.Catalog;

/// <summary>
/// Shares each artist's score snapshot across one request, including ranking
/// selection and ordinary show enrichment. Global and organic selection scores
/// never replace the artist-local metrics published on a show.
/// </summary>
public sealed class CatalogShowPopularityReader(PopularityService popularity)
{
    // This service is request-scoped. Failed reads remain failed within the request;
    // a retry gets a new scope and can read recovered inputs. Cancellation stops the
    // wait, while the existing popularity service may finish its cache refresh.
    private readonly Dictionary<Guid, Task<Dictionary<Guid, CachedShowPopularity>>> maps = [];

    internal Task<Dictionary<Guid, CachedShowPopularity>> ReadMap(Guid artist)
    {
        if (!maps.TryGetValue(artist, out var map))
        {
            map = popularity.GetShowPopularityCacheMapForArtist(artist);
            maps.Add(artist, map);
        }
        return map;
    }

    public async Task Apply(IReadOnlyList<CatalogShow> shows, CancellationToken cancellationToken)
    {
        foreach (var group in shows.GroupBy(show => show.artist_uuid))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var map = await ReadMap(group.Key).WaitAsync(cancellationToken);
            foreach (var show in group)
            {
                show.Popularity = map.TryGetValue(show.uuid, out var cached)
                    ? CachedShowPopularityMapper.ToPopularityMetrics(cached)
                    : null;
            }
        }
    }
}
