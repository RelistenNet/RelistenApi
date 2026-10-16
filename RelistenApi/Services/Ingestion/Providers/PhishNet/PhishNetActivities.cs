using System.Threading.Tasks;
using Relisten.Api.Models;
using Relisten.Import;
using Temporalio.Activities;

namespace Relisten.Ingestion.Providers.PhishNet;

/// <summary>Selection and per-source enrichment are separate retry boundaries.</summary>
public sealed class PhishNetActivities(ProviderActivityRunner runner)
{
    [Activity("PhishNet.SelectSources")]
    public Task<DiscoveredItem<CatalogSourceId, PhishNetSourceInput>[]> SelectSourcesAsync(
        ProviderRequest<RefreshPhishNetArtist> input
    ) =>
        runner.RunAsync<PhishNetImporter, DiscoveredItem<CatalogSourceId, PhishNetSourceInput>[]>(
            new(input.Request.Artist, input.Execution),
            UpstreamProvider.PhishNet,
            (importer, provider) => importer.SelectSourcesAsync(provider, input.Request.Year)
        );

    [Activity("PhishNet.EnrichSource")]
    public Task<PhishNetItemResult> EnrichSourceAsync(
        ProviderWorkflowContext context,
        DiscoveredItem<CatalogSourceId, PhishNetSourceInput> item
    ) =>
        runner.RunAsync<PhishNetImporter, PhishNetItemResult>(
            context,
            UpstreamProvider.PhishNet,
            (importer, provider) => importer.EnrichSourceAsync(provider, item)
        );
}
