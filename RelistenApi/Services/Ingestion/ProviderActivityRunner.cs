using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Relisten.Api.Models;
using Relisten.Data;
using Relisten.Import;
using Relisten.Ingestion.Providers.Archive;
using Relisten.Ingestion.Providers.Jerry;
using Relisten.Ingestion.Providers.Local;
using Relisten.Ingestion.Providers.PhantasyTour;
using Relisten.Ingestion.Providers.PhishIn;
using Relisten.Ingestion.Providers.PhishNet;
using Relisten.Ingestion.Providers.SetlistFm;
using Temporalio.Activities;
using Temporalio.Exceptions;

namespace Relisten.Ingestion;

/// <summary>Creates one importer scope per activity, including heartbeat and cancellable HTTP lifetime.</summary>
public sealed class ProviderActivityRunner(
    IServiceScopeFactory scopes,
    IngestionHttpClientFactory httpClients,
    ILogger<ProviderActivityRunner> log
)
{
    /// <summary>
    /// Runs one provider operation with fresh importer state and the activity's cancellation token.
    /// The operation fetches HTTP inputs before using ProviderContext.CommitAsync for catalog writes.
    /// </summary>
    public async Task<TResult> RunAsync<TImporter, TResult>(
        ProviderWorkflowContext input,
        UpstreamProvider provider,
        Func<TImporter, ProviderContext, Task<TResult>> action
    )
        where TImporter : ImporterBase
    {
        using var heartbeat = new ActivityHeartbeat();
        var activity = ActivityExecutionContext.Current;
        using var logging = log.BeginScope(
            new Dictionary<string, object?>
            {
                ["ExecutionId"] = input.Execution.Id,
                ["Artist"] = input.Artist.ToString(),
                ["Provider"] = provider.ToValue(),
                ["WorkflowId"] = activity.Info.WorkflowId,
                ["ActivityId"] = activity.Info.ActivityId,
                ["Attempt"] = activity.Info.Attempt,
            }
        );

        RequireActive(input.Execution, activity.CancellationToken);

        // Importer dictionaries are mutable caches. Never share them between
        // activity attempts, even when Temporal retries the same selected item.
        using var scope = scopes.CreateScope();
        var services = scope.ServiceProvider;
        var context = await ResolveContextAsync(
            services,
            input,
            provider,
            activity.CancellationToken
        );
        var importer = services.GetRequiredService<TImporter>();
        importer.UseIngestionHttpClient(httpClients.Create(activity.CancellationToken));

        try
        {
            var result = await action(importer, context);
            LogItemResult(result);
            return result;
        }
        catch (EnrichmentInputsChangedException error)
        {
            throw new ApplicationFailureException(error.Message, "EnrichmentInputsChanged");
        }
        catch (CatalogImportExpiredException error)
        {
            throw new ApplicationFailureException(
                error.Message,
                "ImportExpired",
                nonRetryable: true
            );
        }
    }

    private static async Task<ProviderContext> ResolveContextAsync(
        IServiceProvider services,
        ProviderWorkflowContext input,
        UpstreamProvider provider,
        CancellationToken cancellationToken
    )
    {
        var artist =
            await services
                .GetRequiredService<ArtistService>()
                .FindArtistForImport(input.Artist.ToString())
            ?? throw new ApplicationFailureException(
                "Artist no longer exists.",
                "ArtistNotFound",
                nonRetryable: true
            );
        var mapping =
            artist.upstream_sources.SingleOrDefault(source =>
                source.upstream_source.Provider == provider
            )
            ?? throw new ApplicationFailureException(
                "Artist no longer has the requested provider mapping.",
                "UnsupportedImportOperation",
                nonRetryable: true
            );

        return new ProviderContext(
            artist,
            mapping,
            input.Execution,
            services.GetRequiredService<CatalogImportTransaction>(),
            cancellationToken
        );
    }

    private static void RequireActive(
        IngestionExecution execution,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (DateTimeOffset.UtcNow >= execution.Deadline)
        {
            throw new ApplicationFailureException(
                "The import execution deadline has passed.",
                "ImportExpired",
                nonRetryable: true
            );
        }
    }

    // Result records retain their provider-specific identities. This explicit map adds
    // diagnostics without putting logging members on the Temporal wire contracts.
    private void LogItemResult<TResult>(TResult result)
    {
        (string Key, ItemDisposition Disposition, string? SkipReason)? item = result switch
        {
            ArchiveItemResult value => (
                value.Item.Value,
                value.Disposition,
                value.SkipReason?.ToString()
            ),
            PhishInItemResult value => (
                value.Item.Value.ToString(CultureInfo.InvariantCulture),
                value.Disposition,
                null
            ),
            PhishNetItemResult value => (
                value.Source.Value.ToString(CultureInfo.InvariantCulture),
                value.Disposition,
                null
            ),
            SetlistFmItemResult value => (value.Id.Value, value.Disposition, null),
            PhantasyTourItemResult value => (
                value.Id.Value.ToString(CultureInfo.InvariantCulture),
                value.Disposition,
                null
            ),
            JerryItemResult value => (
                value.Id.Value,
                value.Disposition,
                value.SkipReason?.ToString()
            ),
            LocalItemResult value => (value.Item.Value, value.Disposition, null),
            _ => null,
        };

        if (item is { } outcome)
        {
            log.LogInformation(
                ImportEvents.ItemCompleted,
                "Item {ItemIdentifier} completed with {Disposition}; skip reason: {SkipReason}",
                outcome.Key,
                outcome.Disposition,
                outcome.SkipReason
            );
        }
    }
}
