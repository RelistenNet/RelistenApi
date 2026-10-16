using System.Threading.Tasks;
using Relisten.Api.Models;
using Relisten.Import;
using Temporalio.Activities;

namespace Relisten.Ingestion.Providers.Jerry;

/// <summary>Provider I/O retry boundaries. Each operation resolves current artist and mapping state.</summary>
public sealed class JerryActivities(ProviderActivityRunner runner)
{
    [Activity("Jerry.SelectMissingPages")]
    public Task<JerrySelection> SelectMissingPagesAsync(ProviderWorkflowContext context) =>
        runner.RunAsync<JerryGarciaComImporter, JerrySelection>(
            context,
            UpstreamProvider.JerryGarcia,
            (importer, provider) => importer.SelectPagesAsync(provider, false)
        );

    [Activity("Jerry.SelectExistingPages")]
    public Task<JerrySelection> SelectExistingPagesAsync(ProviderWorkflowContext context) =>
        runner.RunAsync<JerryGarciaComImporter, JerrySelection>(
            context,
            UpstreamProvider.JerryGarcia,
            (importer, provider) => importer.SelectPagesAsync(provider, true)
        );

    [Activity("Jerry.ImportPage")]
    public Task<JerryItemResult> ImportPageAsync(
        ProviderWorkflowContext context,
        DiscoveredItem<JerryShowId, JerryGarciaComImporter.FileMetaObject> item
    ) =>
        runner.RunAsync<JerryGarciaComImporter, JerryItemResult>(
            context,
            UpstreamProvider.JerryGarcia,
            (importer, provider) => importer.ImportPageAsync(provider, item)
        );

    [Activity("Jerry.RepairVenue")]
    public Task<JerryItemResult> RepairVenueAsync(
        ProviderWorkflowContext context,
        DiscoveredItem<JerryShowId, JerryGarciaComImporter.FileMetaObject> item
    ) =>
        runner.RunAsync<JerryGarciaComImporter, JerryItemResult>(
            context,
            UpstreamProvider.JerryGarcia,
            (importer, provider) => importer.RepairVenueAsync(provider, item)
        );

    [Activity("Jerry.FinishVenueRepair")]
    public Task<int> FinishVenueRepairAsync(ProviderWorkflowContext context, bool removeOrphans) =>
        runner.RunAsync<JerryGarciaComImporter, int>(
            context,
            UpstreamProvider.JerryGarcia,
            (importer, provider) => importer.FinishVenueRepairAsync(provider, removeOrphans)
        );
}
