using System.Threading.Tasks;
using Relisten.Api.Models;
using Relisten.Ingestion.Workflows;
using Temporalio.Workflows;

namespace Relisten.Ingestion.Providers.PhishIn;

/// <summary>Starts the next page after the preceding page has settled.</summary>
public sealed record PhishInProgress(PhishInPage NextPage, ProviderSummary Summary);

/// <summary>
/// Prepares shared catalogs, then imports Phish.in show pages sequentially.
/// The artist coordinator owns rebuilding. When its plan includes Phish.net,
/// that enrichment runs after this provider completes.
/// </summary>
[Workflow]
public sealed class PhishInWorkflow
{
    private ProviderRequest<RefreshPhishInArtist> _input = null!;
    private ProviderWorkflowContext _context = null!;
    private ActivityOptions _options = null!;
    private PhishInCatalogs _catalogs = null!;
    private ProviderSummary _summary = null!;
    private int _activityCalls;

    [WorkflowRun]
    public async Task<ProviderSummary> RunAsync(
        ProviderRequest<RefreshPhishInArtist> input,
        PhishInCatalogs? catalogs = null,
        PhishInProgress? progress = null
    )
    {
        _input = input;
        _context = new ProviderWorkflowContext(input.Request.Artist, input.Execution);
        _options = ImportWorkflowOptions.Activity(
            IngestionQueues.Providers[UpstreamProvider.Phishin]
        );
        _summary = progress?.Summary ?? new();

        ImportWorkflowOptions.RequireActive(input.Execution);
        _catalogs = catalogs ?? await PrepareCatalogsAsync();
        await ImportShowPagesAsync(progress?.NextPage);

        return _summary;
    }

    private async Task<PhishInCatalogs> PrepareCatalogsAsync()
    {
        // Shows depend on these venues, songs, and tours. Retain their mappings
        // across continuation instead of preparing them again for each run.
        var catalogs = await Workflow.ExecuteActivityAsync(
            (PhishInActivities activities) => activities.PrepareCatalogsAsync(_context),
            _options
        );
        _activityCalls++;
        return catalogs;
    }

    private async Task ImportShowPagesAsync(PhishInPage? nextPage)
    {
        do
        {
            ImportWorkflowOptions.RequireActive(_input.Execution);
            var page = await ReadPageAsync(nextPage);
            await ImportPageAsync(page);

            // A filtered refresh can start with an empty probe page. Only the
            // provider's cursor determines when enumeration is complete.
            nextPage = page.Next;
            if (nextPage != null && ImportWorkflowOptions.ShouldContinue(_activityCalls))
            {
                var checkpoint = new PhishInProgress(nextPage, _summary);
                throw Workflow.CreateContinueAsNewException(
                    (PhishInWorkflow workflow) => workflow.RunAsync(_input, _catalogs, checkpoint)
                );
            }
        } while (nextPage != null);
    }

    private async Task<
        Page<DiscoveredItem<PhishInShowId, PhishInShowInput>, PhishInPage>
    > ReadPageAsync(PhishInPage? cursor)
    {
        var page = await Workflow.ExecuteActivityAsync(
            (PhishInActivities activities) => activities.ReadShowPageAsync(_input, cursor),
            _options
        );
        _activityCalls++;
        return page;
    }

    private async Task ImportPageAsync(
        Page<DiscoveredItem<PhishInShowId, PhishInShowInput>, PhishInPage> page
    )
    {
        // Complete a page before rollover. Temporal replays its recorded items
        // after a restart; the soft history limit can overshoot by one 20-show page.
        await ImportItemOperation.RunConcurrentlyAsync(
            page.Items,
            _input.Execution,
            ImportShowAsync
        );
    }

    private async Task ImportShowAsync(DiscoveredItem<PhishInShowId, PhishInShowInput> item)
    {
        var outcome = await ImportItemOperation.RunAsync(
            item.Id,
            () =>
                Workflow.ExecuteActivityAsync(
                    (PhishInActivities activities) =>
                        activities.ImportShowAsync(_context, _catalogs, item),
                    _options
                )
        );
        _activityCalls++;
        _summary =
            outcome.Failure == null
                ? _summary.Add(outcome.Result!.Disposition)
                : _summary.AddFailure(outcome.Failure);
    }
}
