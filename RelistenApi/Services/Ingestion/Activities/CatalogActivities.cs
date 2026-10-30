using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Npgsql;
using Relisten.Api.Models;
using Relisten.Data;
using Relisten.Normalization.Venues;
using Temporalio.Activities;
using Temporalio.Exceptions;

namespace Relisten.Ingestion;

/// <summary>Resolves the current artist and owns the three independently retryable catalog rebuild steps.</summary>
public sealed class CatalogActivities(
    ArtistService artists,
    CatalogRebuildService rebuild,
    ArtistVenueCounts artistVenues,
    ILogger<CatalogActivities> logger
)
{
    [Activity("Catalog.PrepareArtist")]
    public async Task<ArtistImportPlan> PrepareArtist(
        ArtistRefreshCommand request,
        IngestionExecution execution
    )
    {
        using var heartbeat = new ActivityHeartbeat();
        var artist = await Resolve(new ProviderWorkflowContext(request.Artist, execution));

        var requested = request switch
        {
            RefreshArchiveArtist => UpstreamProvider.ArchiveOrg,
            RefreshPhishInArtist => UpstreamProvider.Phishin,
            RefreshPhishNetArtist => UpstreamProvider.PhishNet,
            RefreshSetlistFmArtist => UpstreamProvider.SetlistFm,
            RefreshPhantasyTourArtist => UpstreamProvider.PhantasyTour,
            RefreshJerryArtist => UpstreamProvider.JerryGarcia,
            RefreshLocalArtist => UpstreamProvider.Local,
            _ => (UpstreamProvider?)null,
        };

        var providers = artist
            .upstream_sources.Select(source => source.upstream_source.Provider)
            .Where(provider =>
                provider.HasValue && IngestionQueues.Providers.ContainsKey(provider.Value)
            )
            .Select(provider => provider!.Value)
            .Where(provider => requested == null || provider == requested)
            .ToArray();

        if (requested.HasValue && providers.Length == 0)
        {
            throw new ApplicationFailureException(
                "Artist has no mapping for the requested provider.",
                "UnsupportedImportOperation",
                nonRetryable: true
            );
        }

        return new ArtistImportPlan(request.Artist, providers);
    }

    [Activity("Catalog.RebuildShows")]
    public async Task RebuildShows(ProviderWorkflowContext context)
    {
        using var heartbeat = new ActivityHeartbeat();
        await rebuild.RebuildShows(await Resolve(context));
        RequireActive(context.Execution);
    }

    [Activity("Catalog.RebuildYears")]
    public async Task RebuildYears(ProviderWorkflowContext context)
    {
        using var heartbeat = new ActivityHeartbeat();
        await rebuild.RebuildYears(await Resolve(context));
        RequireActive(context.Execution);
    }

    [Activity("Catalog.InvalidateArtistCache")]
    public async Task InvalidateArtistCache(ProviderWorkflowContext context)
    {
        using var heartbeat = new ActivityHeartbeat();
        await rebuild.InvalidateArtistCache(await Resolve(context));
        RequireActive(context.Execution);

        var cancellationToken = ActivityExecutionContext.Current.CancellationToken;
        try
        {
            await artistVenues.RefreshArtistsAsync([context.Artist.Value], cancellationToken);
        }
        catch (Exception exception)
            when (!cancellationToken.IsCancellationRequested
                && exception is NpgsqlException or TimeoutException
            )
        {
            // Catalog rebuilds already committed. Source triggers retained pending work;
            // scheduled refresh/reconciliation repairs counts without failing the import.
            logger.LogWarning(
                exception,
                "Artist venue counts remain pending after rebuilding {Artist}",
                context.Artist
            );
        }
        RequireActive(context.Execution);
    }

    private async Task<Artist> Resolve(ProviderWorkflowContext context)
    {
        RequireActive(context.Execution);
        var artist =
            await artists.FindArtistForImport(context.Artist.ToString())
            ?? throw new ApplicationFailureException(
                "Artist no longer exists.",
                "ArtistNotFound",
                nonRetryable: true
            );

        RequireActive(context.Execution);
        return artist;
    }

    private static void RequireActive(IngestionExecution execution)
    {
        ActivityExecutionContext.Current.CancellationToken.ThrowIfCancellationRequested();
        if (DateTimeOffset.UtcNow >= execution.Deadline)
        {
            throw new ApplicationFailureException(
                "This import has expired. Start a fresh import.",
                "ImportExpired",
                nonRetryable: true
            );
        }
    }
}
