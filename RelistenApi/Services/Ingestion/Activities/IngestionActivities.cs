using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Relisten.Data;
using Relisten.Import;
using Temporalio.Activities;
using Temporalio.Exceptions;

namespace Relisten.Ingestion;

/// <summary>Runs provider operations with scoped services and stores large inputs outside workflow history.</summary>
public sealed class IngestionActivities(
    ArtistService artists,
    ImporterService importers,
    ImportRunRepository store,
    IConfiguration configuration
) : IIngestionActivities
{
    [Activity("Ingestion.Prepare")]
    public async Task<ArtistImportPlan> Prepare(ArtistImportRequest request)
    {
        var previous = await store.ReadAsync<ArtistImportPlan>(request.RequestId, "plan");
        if (previous != null)
            return previous;
        var artist =
            await artists.FindArtistWithIdOrSlug(request.Artist)
            ?? throw new ApplicationFailureException("Artist not found.", nonRetryable: true);
        var providers = artist
            .upstream_sources.Select(s => s.upstream_source.name)
            .Where(IngestionQueues.Providers.ContainsKey)
            .Where(p => request.Provider == null || p == request.Provider)
            .ToArray();
        var plan = new ArtistImportPlan(artist.uuid.ToString(), providers);
        await store.SaveAsync(request.RequestId, "request", request);
        await store.SaveAsync(request.RequestId, "plan", plan);
        return plan;
    }

    [Activity("Ingestion.Discover")]
    public async Task<ImportPage> Discover(
        ArtistImportRequest request,
        string provider,
        string? cursor
    )
    {
        var key = provider + "/page/" + (cursor ?? "first");
        var previous = await store.ReadAsync<ImportPage>(request.RequestId, key);
        if (previous != null)
            return previous;
        var (context, importer) = await Context(request, provider);
        var page = await ((IProviderIngestion)importer).DiscoverAsync(context, cursor);
        return await store.SavePageAsync(request.RequestId, provider, key, page);
    }

    [Activity("Ingestion.Fetch")]
    public async Task<bool> Fetch(ImportItemRef item, int part)
    {
        var key = ItemKey(item) + "/fetch/" + part;
        var previous = await store.ReadAsync<ProviderFetch>(item.Request.RequestId, key);
        if (previous != null)
            return previous.Done;
        var (context, importer) = await Context(item.Request, item.Provider);
        var input = await Input(item);
        var fetched = await ((IProviderIngestion)importer).FetchAsync(context, input, part);
        await store.SaveAsync(item.Request.RequestId, key, fetched);
        return fetched.Done;
    }

    [Activity("Ingestion.Apply")]
    public async Task<ImportStats> Apply(ImportItemRef item)
    {
        var (context, importer) = await Context(item.Request, item.Provider);
        var input = await Input(item);
        var fetched = new System.Collections.Generic.List<ProviderFetch>();
        for (var part = 0; ; part++)
        {
            var result =
                await store.ReadAsync<ProviderFetch>(
                    item.Request.RequestId,
                    ItemKey(item) + "/fetch/" + part
                ) ?? throw new InvalidOperationException("Item dependency was not staged.");
            fetched.Add(result);
            if (result.Done)
                break;
        }
        return await store.ApplyOnceAsync(
            item.Request.RequestId,
            ItemKey(item) + "/receipt",
            () => ((IProviderIngestion)importer).ApplyAsync(context, input, fetched.ToArray())
        );
    }

    [Activity("Ingestion.RecordFailure")]
    public Task RecordFailure(ImportItemRef item, string reason) =>
        store.SaveAsync(item.Request.RequestId, ItemKey(item) + "/failure", new { reason });

    [Activity("Ingestion.Candidates")]
    public async Task<string[]> Candidates(ArtistImportRequest request, string provider)
    {
        if (
            provider != ArchiveOrgImporter.DataSourceName
            || request.Year != null
            || request.Item != null
        )
            return Array.Empty<string>();
        var (context, importer) = await Context(request, provider);
        return await ((ArchiveOrgImporter)importer).IngestionDeletionCandidates(context);
    }

    [Activity("Ingestion.Verify")]
    public async Task Verify(ArtistImportRequest request, string provider, string key)
    {
        var (context, importer) = await Context(request, provider);
        await ((ArchiveOrgImporter)importer).VerifyIngestionCandidate(context, key);
    }

    [Activity("Ingestion.FinishProvider")]
    public async Task<ImportStats> FinishProvider(ArtistImportRequest request, string provider)
    {
        var (context, importer) = await Context(request, provider);
        return await store.ApplyOnceAsync(
            request.RequestId,
            provider + "/finished",
            async () =>
                importer is ArchiveOrgImporter archive
                    ? await archive.FinishIngestion(context)
                    : new ImportStats()
        );
    }

    [Activity("Ingestion.FinishArtist")]
    public async Task FinishArtist(ArtistImportRequest request, ImportResult result)
    {
        var artist =
            await artists.FindArtistWithIdOrSlug(request.Artist)
            ?? throw new ApplicationFailureException("Artist not found.", nonRetryable: true);
        await store.ApplyOnceAsync(
            request.RequestId,
            "finished",
            async () =>
            {
                await importers.Rebuild(artist, null);
                return result;
            }
        );
    }

    private static string ItemKey(ImportItemRef item) => item.Page + "/item/" + item.Index;

    private async Task<ProviderItem> Input(ImportItemRef item) =>
        await store.ReadAsync<ProviderItem>(item.Request.RequestId, ItemKey(item))
        ?? throw new InvalidOperationException("Item was not staged.");

    private async Task<(ProviderContext, ImporterBase)> Context(
        ArtistImportRequest request,
        string provider
    )
    {
        var artist =
            await artists.FindArtistWithIdOrSlug(request.Artist)
            ?? throw new ApplicationFailureException("Artist not found.", nonRetryable: true);
        var source = artist.upstream_sources.Single(s => s.upstream_source.name == provider);
        var importer = source.upstream_source.importer!;
        var endpoint = configuration["Ingestion:UpstreamBaseUrl"];
        importer.UseIngestionHttpClient(
            new HttpClient(
                new IngestionHttpHandler(
                    ActivityExecutionContext.Current.CancellationToken,
                    endpoint == null ? null : new Uri(endpoint)
                )
            )
            {
                Timeout = TimeSpan.FromSeconds(60),
            }
        );
        importer.CurrentImportOptions = new ImportOptions { OnlyYear = request.Year };
        return (
            new ProviderContext(
                artist,
                source,
                request,
                store,
                ActivityExecutionContext.Current.CancellationToken
            ),
            importer
        );
    }
}
