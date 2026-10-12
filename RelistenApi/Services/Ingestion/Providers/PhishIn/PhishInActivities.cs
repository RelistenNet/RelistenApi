using System.Threading.Tasks;
using Relisten.Api.Models;
using Relisten.Import;
using Temporalio.Activities;

namespace Relisten.Ingestion.Providers.PhishIn;

/// <summary>Phish.in I/O boundaries; prepared mappings are an independent, reusable payload.</summary>
public sealed class PhishInActivities(ProviderActivityRunner runner)
{
    [Activity("PhishIn.PrepareCatalogs")]
    public Task<PhishInCatalogs> PrepareCatalogsAsync(ProviderWorkflowContext context) =>
        runner.RunAsync<PhishinImporter, PhishInCatalogs>(
            context,
            UpstreamProvider.Phishin,
            (importer, provider) => importer.PrepareCatalogsAsync(provider)
        );

    [Activity("PhishIn.ReadShowPage")]
    public Task<
        Page<DiscoveredItem<PhishInShowId, PhishInShowInput>, PhishInPage>
    > ReadShowPageAsync(ProviderRequest<RefreshPhishInArtist> input, PhishInPage? page) =>
        runner.RunAsync<
            PhishinImporter,
            Page<DiscoveredItem<PhishInShowId, PhishInShowInput>, PhishInPage>
        >(
            new(input.Request.Artist, input.Execution),
            UpstreamProvider.Phishin,
            (importer, provider) =>
                importer.ReadShowPageAsync(provider, input.Request.FromYear, page)
        );

    [Activity("PhishIn.ImportShow")]
    public Task<PhishInItemResult> ImportShowAsync(
        ProviderWorkflowContext context,
        PhishInCatalogs catalogs,
        DiscoveredItem<PhishInShowId, PhishInShowInput> item
    ) =>
        runner.RunAsync<PhishinImporter, PhishInItemResult>(
            context,
            UpstreamProvider.Phishin,
            (importer, provider) => importer.ImportShowAsync(provider, catalogs, item)
        );
}
