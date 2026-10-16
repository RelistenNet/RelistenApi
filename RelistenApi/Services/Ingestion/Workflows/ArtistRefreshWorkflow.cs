using System.Threading.Tasks;
using Relisten.Api.Models;
using Relisten.Ingestion.Providers.Archive;
using Relisten.Ingestion.Providers.Jerry;
using Relisten.Ingestion.Providers.Local;
using Relisten.Ingestion.Providers.PhantasyTour;
using Relisten.Ingestion.Providers.PhishIn;
using Relisten.Ingestion.Providers.PhishNet;
using Relisten.Ingestion.Providers.SetlistFm;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace Relisten.Ingestion.Workflows;

/// <summary>Runs the artist's concrete providers in catalog order, then rebuilds derived shows, years, and cache once.</summary>
[Workflow]
public class ArtistRefreshWorkflow
{
    [WorkflowRun]
    public async Task<ArtistImportResult> RunAsync(
        ArtistRefreshCommand request,
        IngestionExecution? execution = null
    )
    {
        execution = ImportWorkflowOptions.AssignExecution(execution);
        ImportWorkflowOptions.RequireActive(execution);

        var plan = await Workflow.ExecuteActivityAsync(
            (CatalogActivities activities) => activities.PrepareArtist(request, execution),
            ImportWorkflowOptions.Activity(IngestionQueues.Database)
        );

        ArtistImportResult result;
        try
        {
            result = await RefreshProvidersAsync(request, execution, plan.Providers);
        }
        catch (ChildWorkflowFailureException error)
            when (!TemporalException.IsCanceledException(error)
                && !ImportWorkflowOptions.ContainsExpiration(error)
            )
        {
            // A provider can fail after it or an earlier provider committed sources.
            // Restore their show/year associations before reporting the failed import;
            // another provider's outage must not hide already imported recordings.
            await CatalogSteps.RebuildArtist(request.Artist, execution);
            throw;
        }

        // Partial and empty imports also repair derived data left by earlier failed work.
        await CatalogSteps.RebuildArtist(request.Artist, execution);

        return result;
    }

    private static async Task<ArtistImportResult> RefreshProvidersAsync(
        ArtistRefreshCommand request,
        IngestionExecution execution,
        UpstreamProvider[] providers
    )
    {
        var result = new ArtistImportResult(request.Artist, 0, 0, 0);

        // Provider order comes from the catalog. Keep it sequential so later
        // providers can use the artist data written by earlier providers.
        foreach (var provider in providers)
        {
            var summary = await RunProvider(request, execution, provider);

            result = result with
            {
                CompletedItems = result.CompletedItems + summary.CompletedItems,
                SkippedItems = result.SkippedItems + summary.SkippedItems,
                FailedItems = result.FailedItems + summary.FailedItems,
            };
        }

        return result;
    }

    private static Task<ProviderSummary> RunProvider(
        ArtistRefreshCommand request,
        IngestionExecution execution,
        UpstreamProvider provider
    )
    {
        var artist = request.Artist;
        var filteredYear = (request as RefreshFilteredArtist)?.Year;
        var options = CoordinatorOptions.Child(
            $"{Workflow.Info.WorkflowId}/provider/{provider.ToValue()}",
            execution
        );

        // Build the typed request before starting its child. Only the three
        // providers below accept year filters; the others refresh the whole artist.
        switch (provider)
        {
            case UpstreamProvider.ArchiveOrg:
            {
                var command =
                    request as RefreshArchiveArtist
                    ?? new RefreshArchiveArtist(artist, filteredYear);
                var providerRequest = new ProviderRequest<RefreshArchiveArtist>(command, execution);

                return Workflow.ExecuteChildWorkflowAsync(
                    (ArchiveWorkflow workflow) => workflow.RunAsync(providerRequest, null, null),
                    options
                );
            }

            case UpstreamProvider.Phishin:
            {
                var command =
                    request as RefreshPhishInArtist
                    ?? new RefreshPhishInArtist(artist, filteredYear);
                var providerRequest = new ProviderRequest<RefreshPhishInArtist>(command, execution);

                return Workflow.ExecuteChildWorkflowAsync(
                    (PhishInWorkflow workflow) => workflow.RunAsync(providerRequest, null, null),
                    options
                );
            }

            case UpstreamProvider.PhishNet:
            {
                var command =
                    request as RefreshPhishNetArtist
                    ?? new RefreshPhishNetArtist(artist, filteredYear);
                var providerRequest = new ProviderRequest<RefreshPhishNetArtist>(
                    command,
                    execution
                );

                return Workflow.ExecuteChildWorkflowAsync(
                    (PhishNetWorkflow workflow) => workflow.RunAsync(providerRequest, null, null),
                    options
                );
            }

            case UpstreamProvider.SetlistFm:
            {
                var command = new RefreshSetlistFmArtist(artist);
                var providerRequest = new ProviderRequest<RefreshSetlistFmArtist>(
                    command,
                    execution
                );

                return Workflow.ExecuteChildWorkflowAsync(
                    (SetlistFmWorkflow workflow) => workflow.RunAsync(providerRequest, null),
                    options
                );
            }

            case UpstreamProvider.PhantasyTour:
            {
                var command = new RefreshPhantasyTourArtist(artist);
                var providerRequest = new ProviderRequest<RefreshPhantasyTourArtist>(
                    command,
                    execution
                );

                return Workflow.ExecuteChildWorkflowAsync(
                    (PhantasyTourWorkflow workflow) => workflow.RunAsync(providerRequest, null),
                    options
                );
            }

            case UpstreamProvider.JerryGarcia:
            {
                var command = new RefreshJerryArtist(artist);
                var providerRequest = new ProviderRequest<RefreshJerryArtist>(command, execution);

                return Workflow.ExecuteChildWorkflowAsync(
                    (JerryWorkflow workflow) => workflow.RunAsync(providerRequest, null, null),
                    options
                );
            }

            case UpstreamProvider.Local:
            {
                var command = new RefreshLocalArtist(artist);
                var providerRequest = new ProviderRequest<RefreshLocalArtist>(command, execution);

                return Workflow.ExecuteChildWorkflowAsync(
                    (LocalWorkflow workflow) => workflow.RunAsync(providerRequest, null, null),
                    options
                );
            }

            default:
                throw new ApplicationFailureException(
                    "Unsupported ingestion provider.",
                    "UnsupportedImportOperation",
                    nonRetryable: true
                );
        }
    }
}
