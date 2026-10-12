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
    /// Serializes one catalog change with other attempts for this artist and rolls back on failure.
    /// Reload mutable catalog rows inside apply; do not perform HTTP requests while holding the lock.
    /// </summary>
    public Task<T> CommitAsync<T>(Func<Task<T>> apply) =>
        Transactions.RunAsync(Artist.id, Run.Deadline, CancellationToken, apply);
}
