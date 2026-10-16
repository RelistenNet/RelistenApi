using System.Threading.Tasks;
using Relisten.Api.Models;
using Relisten.Ingestion.Workflows;
using Relisten.Vendor.SetlistFm;
using Temporalio.Workflows;

namespace Relisten.Ingestion.Providers.SetlistFm;

/// <summary>Starts the next page after the preceding page has settled.</summary>
public sealed record SetlistFmProgress(SetlistFmPage NextPage, ProviderSummary Summary);

/// <summary>Imports setlist pages, then reconciles tour bounds after every page has settled.</summary>
[Workflow]
public sealed class SetlistFmWorkflow
{
    private ProviderRequest<RefreshSetlistFmArtist> _input = null!;
    private ProviderWorkflowContext _context = null!;
    private ActivityOptions _options = null!;
    private ProviderSummary _summary = null!;
    private int _activityCalls;

    [WorkflowRun]
    public async Task<ProviderSummary> RunAsync(
        ProviderRequest<RefreshSetlistFmArtist> input,
        SetlistFmProgress? progress = null
    )
    {
        _input = input;
        _context = new ProviderWorkflowContext(input.Request.Artist, input.Execution);
        _options = ImportWorkflowOptions.Activity(
            IngestionQueues.Providers[UpstreamProvider.SetlistFm]
        );
        _summary = progress?.Summary ?? new();

        await ImportSetlistPagesAsync(progress?.NextPage);
        await RefreshTourDatesAsync();

        return _summary;
    }

    private async Task ImportSetlistPagesAsync(SetlistFmPage? nextPage)
    {
        do
        {
            ImportWorkflowOptions.RequireActive(_input.Execution);
            var page = await ReadPageAsync(nextPage);
            await ImportPageAsync(page);

            // Empty pages can still have a next cursor. Finish the current page
            // before rollover; the checkpoint needs no page body or item index.
            nextPage = page.Next;
            if (nextPage != null && ImportWorkflowOptions.ShouldContinue(_activityCalls))
            {
                var checkpoint = new SetlistFmProgress(nextPage, _summary);
                throw Workflow.CreateContinueAsNewException(
                    (SetlistFmWorkflow workflow) => workflow.RunAsync(_input, checkpoint)
                );
            }
        } while (nextPage != null);
    }

    private async Task<Page<DiscoveredItem<SetlistFmId, Setlist>, SetlistFmPage>> ReadPageAsync(
        SetlistFmPage? cursor
    )
    {
        var page = await Workflow.ExecuteActivityAsync(
            (SetlistFmActivities activities) => activities.ReadSetlistPageAsync(_context, cursor),
            _options
        );
        _activityCalls++;
        return page;
    }

    private async Task ImportPageAsync(
        Page<DiscoveredItem<SetlistFmId, Setlist>, SetlistFmPage> page
    )
    {
        // Temporal records the fetched page and each completed item in this run's
        // history. Rollover may exceed its soft activity limit by one upstream page.
        await ImportItemOperation.RunConcurrentlyAsync(
            page.Items,
            _input.Execution,
            ImportSetlistAsync
        );
    }

    private Task RefreshTourDatesAsync()
    {
        ImportWorkflowOptions.RequireActive(_input.Execution);

        // A failed listing must not finalize. Item failures can still leave
        // useful committed setlists, whose dates define the cached tour range.
        return Workflow.ExecuteActivityAsync(
            (SetlistFmActivities activities) => activities.RefreshTourDatesAsync(_context),
            _options
        );
    }

    private async Task ImportSetlistAsync(DiscoveredItem<SetlistFmId, Setlist> item)
    {
        var outcome = await ImportItemOperation.RunAsync(
            item.Id,
            () =>
                Workflow.ExecuteActivityAsync(
                    (SetlistFmActivities activities) =>
                        activities.ImportSetlistAsync(_context, item),
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
