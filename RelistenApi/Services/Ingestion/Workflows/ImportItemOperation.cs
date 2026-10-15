using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace Relisten.Ingestion.Workflows;

/// <summary>
/// Bounds concurrent item activities and drains them before returning. Only exhausted
/// item failures become partial results; expiry and cancellation end the provider.
/// </summary>
internal static class ImportItemOperation
{
    // Fixed selections checkpoint only after a complete segment. This bounds
    // history without carrying in-flight work or holes into another run.
    internal const int CheckpointBatchSize = 100;
    private const int ConcurrentItems = 10;

    internal static async Task RunConcurrentlyAsync<TItem>(
        IReadOnlyList<TItem> items,
        IngestionExecution execution,
        Func<TItem, Task> import
    )
    {
        var active = new List<Task>();
        var next = 0;

        try
        {
            while (next < items.Count || active.Count > 0)
            {
                while (next < items.Count && active.Count < ConcurrentItems)
                {
                    Workflow.CancellationToken.ThrowIfCancellationRequested();
                    ImportWorkflowOptions.RequireActive(execution);
                    active.Add(import(items[next++]));
                }

                await Workflow.WhenAnyAsync(active);

                // Observe every settled task before admitting another item. Fatal
                // errors stop admission; ordinary item failures are typed results.
                for (var index = active.Count - 1; index >= 0; index--)
                {
                    if (active[index].IsCompleted)
                    {
                        var finished = active[index];
                        active.RemoveAt(index);
                        await finished;
                    }
                }
            }
        }
        catch (Exception)
        {
            try
            {
                // Activities wait for cancellation acknowledgement. Keep the
                // workflow open until all admitted writes have settled, even if
                // one item reports cancellation or expiry before its siblings.
                await Workflow.WhenAllAsync(active);
            }
            catch (Exception)
            {
                // Preserve the original failure after every sibling has settled.
            }

            throw;
        }
    }

    internal static async Task<(TResult? Result, ItemFailure? Failure)> RunAsync<TKey, TResult>(
        TKey key,
        Func<Task<TResult>> run
    )
        where TResult : class
    {
        try
        {
            var result = await run();
            Workflow.Logger.LogInformation("Item {ItemIdentifier} completed", CanonicalKey(key));
            return (result, null);
        }
        catch (ActivityFailureException error) when (!TemporalException.IsCanceledException(error))
        {
            var cause = error.InnerException;
            if (cause is ApplicationFailureException { ErrorType: "ImportExpired" })
            {
                // Expiration ends the execution; it is not an item we may skip.
                throw;
            }

            var message = cause?.Message ?? error.Message;
            var category =
                (cause as ApplicationFailureException)?.ErrorType
                ?? cause?.GetType().Name
                ?? "ActivityFailure";

            Workflow.Logger.LogWarning(
                error,
                "Item {ItemIdentifier} failed after activity retries with {ErrorCategory}",
                CanonicalKey(key),
                category
            );

            return (
                null,
                new ItemFailure<TKey>(
                    key,
                    category,
                    message.Length <= 500 ? message : message[..500]
                )
            );
        }
    }

    private static string CanonicalKey<TKey>(TKey key) =>
        key switch
        {
            ArchiveItemId value => value.Value,
            PhishInShowId value => value.Value.ToString(CultureInfo.InvariantCulture),
            SetlistFmId value => value.Value,
            PhantasyTourShowId value => value.Value.ToString(CultureInfo.InvariantCulture),
            JerryShowId value => value.Value,
            LocalShowDate value => value.Value,
            CatalogSourceId value => value.Value.ToString(CultureInfo.InvariantCulture),
            _ => throw new ArgumentException("Unknown provider item key.", nameof(key)),
        };
}
