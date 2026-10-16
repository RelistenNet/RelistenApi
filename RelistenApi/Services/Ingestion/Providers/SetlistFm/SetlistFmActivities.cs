using System.Threading.Tasks;
using Relisten.Api.Models;
using Relisten.Import;
using Relisten.Vendor.SetlistFm;
using Temporalio.Activities;

namespace Relisten.Ingestion.Providers.SetlistFm;

/// <summary>Provider I/O retry boundaries. Each operation resolves current artist and mapping state.</summary>
public sealed class SetlistFmActivities(ProviderActivityRunner runner)
{
    [Activity("SetlistFm.ReadSetlistPage")]
    public Task<Page<DiscoveredItem<SetlistFmId, Setlist>, SetlistFmPage>> ReadSetlistPageAsync(
        ProviderWorkflowContext context,
        SetlistFmPage? page
    ) =>
        runner.RunAsync<
            SetlistFmImporter,
            Page<DiscoveredItem<SetlistFmId, Setlist>, SetlistFmPage>
        >(
            context,
            UpstreamProvider.SetlistFm,
            (importer, provider) => importer.ReadSetlistPageAsync(provider, page)
        );

    /// <summary>Returns an attempt-local changed-tour count, separate from completed show items.</summary>
    [Activity("SetlistFm.RefreshTourDates")]
    public Task<int> RefreshTourDatesAsync(ProviderWorkflowContext context) =>
        runner.RunAsync<SetlistFmImporter, int>(
            context,
            UpstreamProvider.SetlistFm,
            (importer, provider) => importer.RefreshTourDatesAsync(provider)
        );

    [Activity("SetlistFm.ImportSetlist")]
    public Task<SetlistFmItemResult> ImportSetlistAsync(
        ProviderWorkflowContext context,
        DiscoveredItem<SetlistFmId, Setlist> item
    ) =>
        runner.RunAsync<SetlistFmImporter, SetlistFmItemResult>(
            context,
            UpstreamProvider.SetlistFm,
            (importer, provider) => importer.ImportSetlistAsync(provider, item)
        );
}
