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
    public Task<T> CommitAsync<T>(Func<Task<T>> apply) =>
        Transactions.RunAsync(Artist.id, Run.Deadline, CancellationToken, apply);
}
