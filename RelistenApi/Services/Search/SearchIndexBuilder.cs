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

    public async Task<(Dictionary<string, long> Counts, string[] PreviousCollections)> Build(
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
            // These names are private until the alias phase. A failed import
            // should neither disturb current search nor leave a partial index.
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

            throw;
        }

        var previous = await Publish(aliasPrefix, buildId, cancellationToken);
        return (counts, previous);
    }

    public async Task RemoveRetired(
        string aliasPrefix,
        string[] collections,
        CancellationToken cancellationToken
    )
    {
        foreach (var kind in SearchIndexSchema.Collections)
        {
            var alias = AliasName(aliasPrefix, kind);
            var current = await typesense.ReadAlias(alias, cancellationToken);
            foreach (
                var name in collections.Where(name =>
                    name.StartsWith(alias + "_", StringComparison.Ordinal)
                )
            )
            {
                if (current != name)
                {
                    await typesense.DeleteCollection(name, cancellationToken);
                }
            }
        }
    }

    private async Task<long> ImportCollection(
        string collection,
        IAsyncEnumerable<Document> documents,
        CancellationToken cancellationToken
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
                imported += await ImportBatch(collection, batch, size, cancellationToken);
                if (imported / 250_000 != (imported - size) / 250_000)
                {
                    logger.LogInformation(
                        "Imported {Count:N0} documents into {Collection}",
                        imported,
                        collection
                    );
                }
                batch.Clear();
                size = 0;
            }
        }

        if (size > 0)
        {
            imported += await ImportBatch(collection, batch, size, cancellationToken);
        }

        logger.LogInformation(
            "Imported {Count:N0} documents into {Collection}",
            imported,
            collection
        );
        return imported;
    }

    private async Task<int> ImportBatch(
        string collection,
        StringBuilder batch,
        int expected,
        CancellationToken cancellationToken
    )
    {
        var result = await typesense.Import(
            collection,
            batch.ToString(),
            upsert: false,
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

    private async Task<string[]> Publish(
        string aliasPrefix,
        string buildId,
        CancellationToken cancellationToken
    )
    {
        var previous = new List<string>();
        foreach (var kind in SearchIndexSchema.Collections)
        {
            var alias = AliasName(aliasPrefix, kind);
            var name = CollectionName(aliasPrefix, kind, buildId);
            var old = await typesense.ReadAlias(alias, cancellationToken);
            await typesense.SetAlias(alias, name, cancellationToken);

            if (old != null && old != name)
            {
                previous.Add(old);
            }
        }

        return previous.ToArray();
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
