using Microsoft.Extensions.Logging.Abstractions;
using Relisten;
using Relisten.Services.Popularity;

namespace RelistenApiTests.Services;

/// <summary>Public show browsing still reads popularity; fixture artists have a fresh empty cache snapshot.</summary>
internal sealed class EmptyShowPopularity
{
    private readonly PopularityCacheService cache;
    public PopularityService Service { get; }

    public EmptyShowPopularity(DbService db, RedisService redis)
    {
        cache = new(redis);
        Service = new(db, cache, new IgnoredJobs(), NullLogger<PopularityService>.Instance);
    }

    public Task PrepareArtistAsync(Guid artistUuid) =>
        cache.SetAsync(
            $"popularity:artist:{artistUuid}:shows:map:30d-48h",
            new PopularityCacheEntry<Dictionary<Guid, CachedShowPopularity>>
            {
                data = [],
                generated_at = DateTime.UtcNow,
                stale_after_seconds = 3600,
            }
        );

    private sealed class IgnoredJobs : Hangfire.IBackgroundJobClient
    {
        public string Create(Hangfire.Common.Job job, Hangfire.States.IState state) =>
            "fixture-popularity";

        public bool ChangeState(string jobId, Hangfire.States.IState state, string expectedState) =>
            true;
    }
}
