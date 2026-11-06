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
    private readonly object gate = new();

    public void Require(PopularityPopulationKind kind, System.Guid? artist = null)
    {
        lock (gate)
        {
            required.Add(new(kind, artist));
        }
    }

    public async Task EnqueueStale(CancellationToken cancellationToken)
    {
        PopularityPopulation[] populations;
        lock (gate)
        {
            populations = required.ToArray();
        }
        if (populations.Length == 0)
        {
            return;
        }

        var stale = await new PopularityStore(db).ReadStale(populations, cancellationToken);
        if (stale.Length > 0)
        {
            jobs.Enqueue<PopularityJobs>(job =>
                job.RefreshPopulations(stale, CancellationToken.None)
            );
        }
    }
}
