using System.Linq;
using System.Threading.Tasks;
using Relisten.Api.Models;
using Relisten.Ingestion.Workflows;
using Temporalio.Workflows;

namespace Relisten.Ingestion.Providers.PhishNet;

/// <summary>Resumes enrichment at the next unfinished source in the fixed selection.</summary>
public sealed record PhishNetProgress(
    ProviderStage Stage = ProviderStage.NotStarted,
    int ItemIndex = 0,
    ProviderSummary? Summary = null
);

/// <summary>
/// Enriches existing catalog sources with Phish.net setlists and reviews.
/// The artist coordinator completes Phish.in first so its sources are available for selection.
/// </summary>
[Workflow]
public sealed class PhishNetWorkflow
{
    private ProviderRequest<RefreshPhishNetArtist> _input = null!;
    private ProviderWorkflowContext _context = null!;
    private ActivityOptions _options = null!;
    private DiscoveredItem<CatalogSourceId, PhishNetSourceInput>[] _selection = null!;
    private PhishNetProgress _progress = null!;
    private ProviderSummary _summary = null!;
    private int _completedCalls;

    [WorkflowRun]
    public async Task<ProviderSummary> RunAsync(
        ProviderRequest<RefreshPhishNetArtist> input,
        DiscoveredItem<CatalogSourceId, PhishNetSourceInput>[]? selection = null,
        PhishNetProgress? progress = null
    )
    {
        RestoreCheckpoint(input, selection, progress);
        ImportWorkflowOptions.RequireActive(input.Execution);

        await SelectSourcesIfNeededAsync();
        await EnrichSelectedSourcesAsync();

        return _summary;
    }

    private void RestoreCheckpoint(
        ProviderRequest<RefreshPhishNetArtist> input,
        DiscoveredItem<CatalogSourceId, PhishNetSourceInput>[]? selection,
        PhishNetProgress? progress
    )
    {
        // Replay reconstructs these fields. ContinueAsNew starts a fresh instance,
        // so the fixed selection and durable progress also travel in run arguments.
        _input = input;
        _context = new ProviderWorkflowContext(input.Request.Artist, input.Execution);
        _options = ImportWorkflowOptions.Activity(
            IngestionQueues.Providers[UpstreamProvider.PhishNet]
        );
        _selection = selection!;
        _progress = progress ?? new();
        _summary = _progress.Summary ?? new();
        _completedCalls = 0;
    }

    private async Task SelectSourcesIfNeededAsync()
    {
        if (_progress.Stage != ProviderStage.NotStarted)
        {
            return;
        }

        // Select once so rollover cannot change membership or move the item index
        // when other catalog work adds or updates sources.
        _selection = await Workflow.ExecuteActivityAsync(
            (PhishNetActivities a) => a.SelectSourcesAsync(_input),
            _options
        );
        _completedCalls++;

        _progress = _progress with { Stage = ProviderStage.Importing };
    }

    private async Task EnrichSelectedSourcesAsync()
    {
        while (
            _progress.Stage == ProviderStage.Importing && _progress.ItemIndex < _selection.Length
        )
        {
            ImportWorkflowOptions.RequireActive(_input.Execution);
            ContinueWithCheckpointIfNeeded();

            var segment = _selection
                .Skip(_progress.ItemIndex)
                .Take(ImportItemOperation.CheckpointBatchSize)
                .ToArray();
            await ImportItemOperation.RunConcurrentlyAsync(
                segment,
                _input.Execution,
                EnrichSourceAsync
            );
            _progress = _progress with { ItemIndex = _progress.ItemIndex + segment.Length };
        }
    }

    private async Task EnrichSourceAsync(DiscoveredItem<CatalogSourceId, PhishNetSourceInput> item)
    {
        var outcome = await ImportItemOperation.RunAsync(
            item.Id,
            () =>
                Workflow.ExecuteActivityAsync(
                    (PhishNetActivities a) => a.EnrichSourceAsync(_context, item),
                    _options
                )
        );
        _completedCalls++;

        _summary =
            outcome.Failure == null
                ? _summary.Add(outcome.Result!.Disposition)
                : _summary.AddFailure(outcome.Failure);
    }

    private void ContinueWithCheckpointIfNeeded()
    {
        if (!ImportWorkflowOptions.ShouldContinue(_completedCalls))
        {
            return;
        }

        var checkpoint = _progress with { Summary = _summary };
        throw Workflow.CreateContinueAsNewException(
            (PhishNetWorkflow w) => w.RunAsync(_input, _selection, checkpoint)
        );
    }
}
