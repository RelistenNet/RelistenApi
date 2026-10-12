using System.Threading.Tasks;
using Relisten.Api.Models;
using Relisten.Ingestion.Workflows;
using Temporalio.Workflows;

namespace Relisten.Ingestion.Providers.PhishIn;

/// <summary>Retains an unfinished page and its item index without repeating upstream discovery.</summary>
public sealed record PhishInProgress(
    ProviderStage Stage = ProviderStage.NotStarted,
    Page<DiscoveredItem<PhishInShowId, PhishInShowInput>, PhishInPage>? Page = null,
    int ItemIndex = 0,
    PhishInPage? NextPage = null,
    ProviderSummary? Summary = null
);

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
    private PhishInProgress _progress = null!;
    private ProviderSummary _summary = null!;
    private int _completedCalls;

    [WorkflowRun]
    public async Task<ProviderSummary> RunAsync(
        ProviderRequest<RefreshPhishInArtist> input,
        PhishInCatalogs? catalogs = null,
        PhishInProgress? progress = null
    )
    {
        RestoreCheckpoint(input, catalogs, progress);
        ImportWorkflowOptions.RequireActive(input.Execution);

        await PrepareCatalogsIfNeededAsync();
        await ImportShowPagesAsync();

        return _summary;
    }

    private void RestoreCheckpoint(
        ProviderRequest<RefreshPhishInArtist> input,
        PhishInCatalogs? catalogs,
        PhishInProgress? progress
    )
    {
        // Replay reconstructs these fields. ContinueAsNew starts a fresh instance,
        // so prepared catalogs and durable progress also travel in the run arguments.
        _input = input;
        _context = new ProviderWorkflowContext(input.Request.Artist, input.Execution);
        _options = ImportWorkflowOptions.Activity(
            IngestionQueues.Providers[UpstreamProvider.Phishin]
        );
        _catalogs = catalogs!;
        _progress = progress ?? new();
        _summary = _progress.Summary ?? new();
        _completedCalls = 0;
    }

    private async Task PrepareCatalogsIfNeededAsync()
    {
        if (_progress.Stage != ProviderStage.NotStarted)
        {
            return;
        }

        // Shows depend on these venues, songs, and tours. Preparation failure
        // stops the provider before it begins importing any show.
        _catalogs = await Workflow.ExecuteActivityAsync(
            (PhishInActivities a) => a.PrepareCatalogsAsync(_context),
            _options
        );
        _completedCalls++;

        _progress = _progress with { Stage = ProviderStage.Importing };
    }

    private async Task ImportShowPagesAsync()
    {
        while (_progress.Stage == ProviderStage.Importing)
        {
            ImportWorkflowOptions.RequireActive(_input.Execution);
            ContinueWithCheckpointIfNeeded();

            if (_progress.Page == null)
            {
                await ReadNextShowPageAsync();
                continue;
            }

            if (_progress.ItemIndex < _progress.Page.Items.Length)
            {
                await ImportNextShowAsync();
                continue;
            }

            // An empty page can still have a next cursor. Only the cursor decides
            // whether discovery is complete.
            _progress = _progress with
            {
                Stage =
                    _progress.Page.Next == null ? ProviderStage.Complete : ProviderStage.Importing,
                NextPage = _progress.Page.Next,
                Page = null,
                ItemIndex = 0,
            };
        }
    }

    private async Task ReadNextShowPageAsync()
    {
        var page = await Workflow.ExecuteActivityAsync(
            (PhishInActivities a) => a.ReadShowPageAsync(_input, _progress.NextPage),
            _options
        );
        _completedCalls++;

        // Preserve this exact page until every item has an outcome. Rollover must
        // not refetch a page whose contents could change between runs.
        _progress = _progress with
        {
            Page = page,
            ItemIndex = 0,
        };
    }

    private async Task ImportNextShowAsync()
    {
        var item = _progress.Page!.Items[_progress.ItemIndex];
        var outcome = await ImportItemOperation.RunAsync(
            item.Id,
            () =>
                Workflow.ExecuteActivityAsync(
                    (PhishInActivities a) => a.ImportShowAsync(_context, _catalogs, item),
                    _options
                )
        );
        _completedCalls++;

        _summary =
            outcome.Failure == null
                ? _summary.Add(outcome.Result!.Disposition)
                : _summary.AddFailure(outcome.Failure);
        _progress = _progress with { ItemIndex = _progress.ItemIndex + 1 };
    }

    private void ContinueWithCheckpointIfNeeded()
    {
        if (!ImportWorkflowOptions.ShouldContinue(_completedCalls))
        {
            return;
        }

        var checkpoint = _progress with { Summary = _summary };
        throw Workflow.CreateContinueAsNewException(
            (PhishInWorkflow w) => w.RunAsync(_input, _catalogs, checkpoint)
        );
    }
}
