using System;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace Relisten.Ingestion.Workflows;

/// <summary>Only exhausted item failures become partial results. Discovery, expiry and cancellation propagate.</summary>
internal static class ImportItemOperation
{
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
