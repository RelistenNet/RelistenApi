using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;

namespace Relisten.Services.Popularity;

/// <summary>
/// Request-scoped collection of required populations. After catalog reads finish,
/// one freshness lookup and at most one queued job cover the entire response.
/// Scores are never calculated here, including on a missing population.
/// </summary>
public sealed class PopularityRefreshRequest(DbService db, IBackgroundJobClient jobs)
{
    private readonly HashSet<PopularityPopulation> required = [];

    public void Require(PopularityPopulationKind kind, System.Guid? artist = null) =>
        required.Add(new(kind, artist));

    public async Task EnqueueStale(CancellationToken cancellationToken)
    {
        if (required.Count == 0)
        {
            return;
        }

        var stale = await new PopularityStore(db).ReadStale(required.ToArray(), cancellationToken);
        if (stale.Length > 0)
        {
            jobs.Enqueue<PopularityJobs>(job =>
                job.RefreshPopulations(stale, CancellationToken.None)
            );
        }
    }
}
