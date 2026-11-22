using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Relisten.Search;

using Document = Dictionary<string, object>;

internal sealed class SearchIndexBuilder(
    TypesenseClient typesense,
    ILogger<SearchIndexBuilder> logger
)
{
    private const int BatchSize = 2_500;
    private const int BatchCharacters = 4_000_000;

    public async Task<Dictionary<string, long>> Build(
        string aliasPrefix,
        string buildId,
        SearchBuildDocuments documents,
        CancellationToken cancellationToken
    )
    {
        var aliases = await typesense.ReadAliases(cancellationToken);
        await RemoveUnaliased(aliasPrefix, aliases, cancellationToken);

        var counts = await ImportAll(aliasPrefix, buildId, documents, cancellationToken);

        // A failed publication may have moved some aliases. Keep all its
        // collections so the next sweep can preserve every current target.
        await Publish(aliasPrefix, buildId, cancellationToken);
        return counts;
    }

    private async Task<Dictionary<string, long>> ImportAll(
        string aliasPrefix,
        string buildId,
        SearchBuildDocuments documents,
        CancellationToken cancellationToken
    )
    {
        var counts = new Dictionary<string, long>();
        var created = new List<string>();

        try
        {
            // Reviews feed the performance ranking, and performances accumulate
            // the play totals used by recordings and shows.
            foreach (var (kind, rows) in documents.InBuildOrder())
            {
                var name = CollectionName(aliasPrefix, kind, buildId);
                await typesense.CreateCollection(
                    SearchIndexSchema.Create(kind, name),
                    cancellationToken
                );
                created.Add(name);
                counts.Add(kind, await ImportCollection(name, rows, cancellationToken));
            }
        }
        catch
        {
            // An operator rerun or the next daily build sweeps any leftovers.
            await RollBackImports(created);
            throw;
        }

        return counts;
    }

    private async Task RollBackImports(IEnumerable<string> created)
    {
        // These names are private until publication.
        foreach (var name in created)
        {
            try
            {
                await typesense.DeleteCollection(name, CancellationToken.None);
            }
            catch (Exception error)
            {
                logger.LogWarning(
                    error,
                    "Could not remove incomplete search collection {Collection}",
                    name
                );
            }
        }
    }

    public async Task RemoveRetired(
        string aliasPrefix,
        string publishedBuildId,
        CancellationToken cancellationToken
    )
    {
        var aliases = await typesense.ReadAliases(cancellationToken);
        if (
            SearchIndexSchema.Collections.Any(kind =>
                aliases.GetValueOrDefault(AliasName(aliasPrefix, kind))
                != CollectionName(aliasPrefix, kind, publishedBuildId)
            )
        )
        {
            // A newer publication has its own grace period.
            return;
        }

        await RemoveUnaliased(aliasPrefix, aliases, cancellationToken);
        // Retirement is already complete if compaction times out. The server also compacts daily.
        await typesense.Compact(cancellationToken);
    }

    private async Task RemoveUnaliased(
        string aliasPrefix,
        Dictionary<string, string> aliases,
        CancellationToken cancellationToken
    )
    {
        var collections = await typesense.ReadCollectionNames(cancellationToken);
        var targets = aliases.Values.ToHashSet(StringComparer.Ordinal);
        foreach (var name in collections.Where(name => IsBuildCollection(aliasPrefix, name)))
        {
            if (!targets.Contains(name))
            {
                await typesense.DeleteCollection(name, cancellationToken);
            }
        }
    }

    private static bool IsBuildCollection(string aliasPrefix, string name)
    {
        foreach (var kind in SearchIndexSchema.Collections)
        {
            var prefix = AliasName(aliasPrefix, kind) + "_";
            if (name.StartsWith(prefix, StringComparison.Ordinal))
            {
                return Guid.TryParseExact(name.AsSpan(prefix.Length), "N", out _);
            }
        }

        return false;
    }

    private async Task<long> ImportCollection(
        string collection,
        IAsyncEnumerable<Document> documents,
        CancellationToken cancellationToken,
        bool upsert = false
    )
    {
        var batch = new StringBuilder();
        var size = 0;
        long imported = 0;

        await foreach (var document in documents.WithCancellation(cancellationToken))
        {
            batch.AppendLine(JsonSerializer.Serialize(document, TypesenseClient.Json));
            size++;

            if (size >= BatchSize || batch.Length >= BatchCharacters)
            {
                imported += await ImportBatch(collection, batch, size, upsert, cancellationToken);
                LogImportProgress(collection, imported, size);
                batch.Clear();
                size = 0;
            }
        }

        if (size > 0)
        {
            imported += await ImportBatch(collection, batch, size, upsert, cancellationToken);
        }

        logger.LogInformation(
            "Imported {Count:N0} documents into {Collection}",
            imported,
            collection
        );
        return imported;
    }

    public Task<long> Upsert(
        string collection,
        IAsyncEnumerable<Document> documents,
        CancellationToken cancellationToken
    ) => ImportCollection(collection, documents, cancellationToken, upsert: true);

    private void LogImportProgress(string collection, long imported, int batchSize)
    {
        const int interval = 250_000;
        if (imported / interval != (imported - batchSize) / interval)
        {
            logger.LogInformation(
                "Imported {Count:N0} documents into {Collection}",
                imported,
                collection
            );
        }
    }

    private async Task<int> ImportBatch(
        string collection,
        StringBuilder batch,
        int expected,
        bool upsert,
        CancellationToken cancellationToken
    )
    {
        var result = await typesense.Import(
            collection,
            batch.ToString(),
            upsert,
            cancellationToken
        );
        if (result.Rejected != 0 || result.Accepted != expected)
        {
            throw new InvalidOperationException(
                $"{collection} import accepted {result.Accepted}/{expected} documents, "
                    + $"rejected {result.Rejected}. {result.FirstError}"
            );
        }

        return result.Accepted;
    }

    private async Task Publish(
        string aliasPrefix,
        string buildId,
        CancellationToken cancellationToken
    )
    {
        foreach (var kind in SearchIndexSchema.Collections)
        {
            var alias = AliasName(aliasPrefix, kind);
            var name = CollectionName(aliasPrefix, kind, buildId);
            await typesense.SetAlias(alias, name, cancellationToken);
        }
    }

    internal static string AliasName(string prefix, string kind) => $"{prefix}_{kind}";

    private static string CollectionName(string prefix, string kind, string buildId) =>
        $"{AliasName(prefix, kind)}_{buildId}";
}

internal sealed record SearchBuildDocuments(
    IAsyncEnumerable<Document> Reviews,
    IAsyncEnumerable<Document> Performances,
    IAsyncEnumerable<Document> Recordings,
    IAsyncEnumerable<Document> Entities
)
{
    public IEnumerable<(string Kind, IAsyncEnumerable<Document> Rows)> InBuildOrder()
    {
        yield return (SearchIndexSchema.Reviews, Reviews);
        yield return (SearchIndexSchema.Performances, Performances);
        yield return (SearchIndexSchema.Recordings, Recordings);
        yield return (SearchIndexSchema.Entities, Entities);
    }
}
