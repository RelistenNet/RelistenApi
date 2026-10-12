using System.Threading.Tasks;
using Relisten.Api.Models;
using Relisten.Import;
using Relisten.Vendor.PhantasyTour;
using Temporalio.Activities;

namespace Relisten.Ingestion.Providers.PhantasyTour;

/// <summary>Provider I/O retry boundaries. Each operation resolves current artist and mapping state.</summary>
public sealed class PhantasyTourActivities(ProviderActivityRunner runner)
{
    [Activity("PhantasyTour.ReadShowPage")]
    public Task<PhantasyTourDiscovery> ReadShowPageAsync(
        ProviderWorkflowContext context,
        PhantasyTourPage? page
    ) =>
        runner.RunAsync<PhantasyTourImporter, PhantasyTourDiscovery>(
            context,
            UpstreamProvider.PhantasyTour,
            (importer, provider) => importer.ReadShowPageAsync(provider, page)
        );

    [Activity("PhantasyTour.ImportShow")]
    public Task<PhantasyTourItemResult> ImportShowAsync(
        ProviderWorkflowContext context,
        DiscoveredItem<PhantasyTourShowId, PhantasyTourShowListing> item
    ) =>
        runner.RunAsync<PhantasyTourImporter, PhantasyTourItemResult>(
            context,
            UpstreamProvider.PhantasyTour,
            (importer, provider) => importer.ImportShowAsync(provider, item)
        );

    [Activity("PhantasyTour.RepairListedDates")]
    public Task<int> RepairListedDatesAsync(
        ProviderWorkflowContext context,
        PhantasyTourListedDate[] dates
    ) =>
        runner.RunAsync<PhantasyTourImporter, int>(
            context,
            UpstreamProvider.PhantasyTour,
            (importer, provider) => importer.RepairListedDatesAsync(provider, dates)
        );
}
