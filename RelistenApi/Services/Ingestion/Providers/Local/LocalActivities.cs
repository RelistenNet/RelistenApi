using System.Threading.Tasks;
using Relisten.Api.Models;
using Relisten.Import;
using Relisten.Vendor.Local;
using Temporalio.Activities;

namespace Relisten.Ingestion.Providers.Local;

/// <summary>Separates upstream fetches from locked catalog updates while retaining the root selection input.</summary>
public sealed class LocalActivities(ProviderActivityRunner runner)
{
    [Activity("Local.FetchRootAndPrepareVenues")]
    public Task<LocalRootObject<LocalShow>> FetchRootAndPrepareVenues(
        ProviderWorkflowContext context
    ) =>
        runner.RunAsync<LocalImporter, LocalRootObject<LocalShow>>(
            context,
            UpstreamProvider.Local,
            (i, c) => i.FetchRootAndPrepareVenuesAsync(c)
        );

    [Activity("Local.SelectChangedDates")]
    public Task<LocalSelection> SelectChangedDates(
        ProviderWorkflowContext context,
        LocalRootObject<LocalShow> root
    ) =>
        runner.RunAsync<LocalImporter, LocalSelection>(
            context,
            UpstreamProvider.Local,
            (i, c) => i.SelectChangedDatesAsync(c, root)
        );

    [Activity("Local.ImportDate")]
    public Task<LocalItemResult> ImportDate(
        ProviderWorkflowContext context,
        DiscoveredItem<LocalShowDate, LocalShow[]> item
    ) =>
        runner.RunAsync<LocalImporter, LocalItemResult>(
            context,
            UpstreamProvider.Local,
            (i, c) => i.ImportDateAsync(c, item)
        );
}
