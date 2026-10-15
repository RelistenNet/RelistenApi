using System;
using System.Threading;
using System.Threading.Tasks;
using Relisten.Api.Models;
using Relisten.Data;

namespace Relisten.Ingestion;

/// <summary>Activity-local catalog identity and transaction access. Fetch HTTP inputs before CommitAsync.</summary>
public sealed record ProviderContext(
    Artist Artist,
    ArtistUpstreamSource Source,
    IngestionExecution Run,
    CatalogImportTransaction Transactions,
    CancellationToken CancellationToken
)
{
    /// <summary>
    /// Commits one catalog change atomically and rolls back on failure or cancellation.
    /// Reload mutable catalog rows inside apply; fetch HTTP inputs before the transaction.
    /// </summary>
    public Task<T> CommitAsync<T>(Func<Task<T>> apply) =>
        Transactions.RunAsync(Run.Deadline, CancellationToken, apply);
}
