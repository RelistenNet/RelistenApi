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
        var result = new ArtistImportResult(request.Artist, 0, 0, 0);
        foreach (var provider in plan.Providers)
        {
            var summary = await RunProvider(request, execution, provider);
            result = result with
            {
                CompletedItems = result.CompletedItems + summary.CompletedItems,
                SkippedItems = result.SkippedItems + summary.SkippedItems,
                FailedItems = result.FailedItems + summary.FailedItems,
            };
        }
        // Partial and empty imports also repair derived data left by earlier failed work.
        // Cancellation and failed prerequisites propagate before reaching this step.
        await CatalogSteps.RebuildArtist(request.Artist, execution);
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
        return provider switch
        {
            UpstreamProvider.ArchiveOrg => Workflow.ExecuteChildWorkflowAsync(
                (ArchiveWorkflow w) =>
                    w.RunAsync(
                        new ProviderRequest<RefreshArchiveArtist>(
                            request as RefreshArchiveArtist
                                ?? new RefreshArchiveArtist(artist, filteredYear),
                            execution
                        ),
                        null,
                        null
                    ),
                options
            ),
            UpstreamProvider.Phishin => Workflow.ExecuteChildWorkflowAsync(
                (PhishInWorkflow w) =>
                    w.RunAsync(
                        new ProviderRequest<RefreshPhishInArtist>(
                            request as RefreshPhishInArtist
                                ?? new RefreshPhishInArtist(artist, filteredYear),
                            execution
                        ),
                        null,
                        null
                    ),
                options
            ),
            UpstreamProvider.PhishNet => Workflow.ExecuteChildWorkflowAsync(
                (PhishNetWorkflow w) =>
                    w.RunAsync(
                        new ProviderRequest<RefreshPhishNetArtist>(
                            request as RefreshPhishNetArtist
                                ?? new RefreshPhishNetArtist(artist, filteredYear),
                            execution
                        ),
                        null,
                        null
                    ),
                options
            ),
            UpstreamProvider.SetlistFm => Workflow.ExecuteChildWorkflowAsync(
                (SetlistFmWorkflow w) =>
                    w.RunAsync(
                        new ProviderRequest<RefreshSetlistFmArtist>(
                            new RefreshSetlistFmArtist(artist),
                            execution
                        ),
                        null,
                        null
                    ),
                options
            ),
            UpstreamProvider.PhantasyTour => Workflow.ExecuteChildWorkflowAsync(
                (PhantasyTourWorkflow w) =>
                    w.RunAsync(
                        new ProviderRequest<RefreshPhantasyTourArtist>(
                            new RefreshPhantasyTourArtist(artist),
                            execution
                        ),
                        null,
                        null
                    ),
                options
            ),
            UpstreamProvider.JerryGarcia => Workflow.ExecuteChildWorkflowAsync(
                (JerryWorkflow w) =>
                    w.RunAsync(
                        new ProviderRequest<RefreshJerryArtist>(
                            new RefreshJerryArtist(artist),
                            execution
                        ),
                        null,
                        null
                    ),
                options
            ),
            UpstreamProvider.Local => Workflow.ExecuteChildWorkflowAsync(
                (LocalWorkflow w) =>
                    w.RunAsync(
                        new ProviderRequest<RefreshLocalArtist>(
                            new RefreshLocalArtist(artist),
                            execution
                        ),
                        null,
                        null
                    ),
                options
            ),
            _ => throw new ApplicationFailureException(
                "Unsupported ingestion provider.",
                "UnsupportedImportOperation",
                nonRetryable: true
            ),
        };
    }
}
