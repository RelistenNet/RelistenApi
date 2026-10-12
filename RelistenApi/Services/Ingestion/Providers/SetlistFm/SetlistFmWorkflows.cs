using System.Threading.Tasks;
using Relisten.Api.Models;
using Relisten.Ingestion.Workflows;
using Relisten.Vendor.SetlistFm;
using Temporalio.Workflows;

namespace Relisten.Ingestion.Providers.SetlistFm;

public sealed record SetlistFmProgress(
    ProviderStage Stage = ProviderStage.NotStarted,
    int ItemIndex = 0,
    SetlistFmPage? Next = null,
    ProviderSummary? Summary = null
);

/// <summary>Reads and applies setlists sequentially, retaining a partly consumed page across continuation.</summary>
[Workflow]
public sealed class SetlistFmWorkflow
{
    // Replay restores fields within this run; continuation arguments carry the
    // current page and its next unprocessed item into a fresh run.
    private ProviderRequest<RefreshSetlistFmArtist> _input = null!;
    private ProviderWorkflowContext _context = null!;
    private ActivityOptions _options = null!;
    private Page<DiscoveredItem<SetlistFmId, Setlist>, SetlistFmPage>? _page;
    private SetlistFmProgress _progress = null!;
    private ProviderSummary _summary = null!;
    private int _activityCalls;

    [WorkflowRun]
    public async Task<ProviderSummary> RunAsync(
        ProviderRequest<RefreshSetlistFmArtist> input,
        Page<DiscoveredItem<SetlistFmId, Setlist>, SetlistFmPage>? page = null,
        SetlistFmProgress? progress = null
    )
    {
        _input = input;
        _context = new ProviderWorkflowContext(input.Request.Artist, input.Execution);
        _options = ImportWorkflowOptions.Activity(
            IngestionQueues.Providers[UpstreamProvider.SetlistFm]
        );
        _page = page;
        _progress = progress ?? new();
        _summary = _progress.Summary ?? new();
        _activityCalls = 0;

        await ImportSetlistPagesAsync();

        return _summary;
    }

    private async Task ImportSetlistPagesAsync()
    {
        while (_progress.Stage != ProviderStage.Complete)
        {
            ContinueBeforeNextStepIfNeeded();

            if (_page == null)
            {
                await ReadNextPageAsync();
                continue;
            }

            if (_progress.ItemIndex < _page.Items.Length)
            {
                await ImportNextSetlistAsync();
                continue;
            }

            // An empty page is not necessarily the last page. Follow the provider's
            // next-page cursor only after every item on this page has an outcome.
            _page = null;
            _progress = _progress with
            {
                Stage = _progress.Next == null ? ProviderStage.Complete : ProviderStage.Importing,
                ItemIndex = 0,
            };
        }
    }

    private async Task ReadNextPageAsync()
    {
        _page = await Workflow.ExecuteActivityAsync(
            (SetlistFmActivities a) => a.ReadSetlistPageAsync(_context, _progress.Next),
            _options
        );

        _activityCalls++;
        _progress = _progress with
        {
            Stage = ProviderStage.Importing,
            ItemIndex = 0,
            Next = _page.Next,
        };
    }

    private async Task ImportNextSetlistAsync()
    {
        var item = _page!.Items[_progress.ItemIndex];
        var outcome = await ImportItemOperation.RunAsync(
            item.Id,
            () =>
                Workflow.ExecuteActivityAsync(
                    (SetlistFmActivities a) => a.ImportSetlistAsync(_context, item),
                    _options
                )
        );

        _activityCalls++;
        _summary =
            outcome.Failure == null
                ? _summary.Add(outcome.Result!.Disposition)
                : _summary.AddFailure(outcome.Failure);
        _progress = _progress with { ItemIndex = _progress.ItemIndex + 1 };
    }

    private void ContinueBeforeNextStepIfNeeded()
    {
        ImportWorkflowOptions.RequireActive(_input.Execution);
        if (!ImportWorkflowOptions.ShouldContinue(_activityCalls))
        {
            return;
        }

        // Keep the partially consumed page. Refetching it after continuation could
        // move items between pages and either repeat or miss setlists.
        _progress = _progress with
        {
            Summary = _summary,
        };
        throw Workflow.CreateContinueAsNewException(
            (SetlistFmWorkflow w) => w.RunAsync(_input, _page, _progress)
        );
    }
}
