using System;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Relisten.Api.Models;
using Relisten.Ingestion.Workflows;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Client.Schedules;
using Temporalio.Exceptions;

namespace Relisten.Ingestion;

/// <summary>Admits explicit import operations; completing a start means queued, not imported.</summary>
public sealed class TemporalIngestionClient
{
    private readonly ITemporalClient client;
    private readonly IConfiguration configuration;
    private readonly IImportAdmissionCatalog catalog;

    public TemporalIngestionClient(
        ITemporalClient client,
        IConfiguration configuration,
        IImportAdmissionCatalog catalog
    )
    {
        this.client = client;
        this.configuration = configuration;
        this.catalog = catalog;

        Archive = new ArchiveImportClient(this);
        Jerry = new JerryImportClient(this);
        PhishIn = new ProviderRefreshClient<RefreshPhishInArtist>(RefreshAsync);
        PhishNet = new ProviderRefreshClient<RefreshPhishNetArtist>(RefreshAsync);
        SetlistFm = new ProviderRefreshClient<RefreshSetlistFmArtist>(RefreshAsync);
        PhantasyTour = new ProviderRefreshClient<RefreshPhantasyTourArtist>(RefreshAsync);
        Local = new ProviderRefreshClient<RefreshLocalArtist>(RefreshAsync);
    }

    public bool Enabled =>
        string.Equals(
            configuration["Ingestion:Engine"],
            "Temporal",
            StringComparison.OrdinalIgnoreCase
        );

    public ArchiveImportClient Archive { get; }
    public JerryImportClient Jerry { get; }
    public ProviderRefreshClient<RefreshPhishInArtist> PhishIn { get; }
    public ProviderRefreshClient<RefreshPhishNetArtist> PhishNet { get; }
    public ProviderRefreshClient<RefreshSetlistFmArtist> SetlistFm { get; }
    public ProviderRefreshClient<RefreshPhantasyTourArtist> PhantasyTour { get; }
    public ProviderRefreshClient<RefreshLocalArtist> Local { get; }

    public Task<ImportHandle<ArtistImportResult>> RefreshArtistAsync(RefreshArtist request) =>
        RefreshAsync(request);

    /// <summary>Starts an explicit refresh scope, preserving each provider's filter meaning.</summary>
    public Task<ImportHandle<ArtistImportResult>> RefreshAsync(ArtistRefreshCommand request)
    {
        var provider = request switch
        {
            RefreshArchiveArtist r => ValidateYear(r.Year, UpstreamProvider.ArchiveOrg),
            RefreshPhishInArtist r => ValidateYear(r.FromYear, UpstreamProvider.Phishin),
            RefreshPhishNetArtist r => ValidateYear(r.Year, UpstreamProvider.PhishNet),
            RefreshSetlistFmArtist => UpstreamProvider.SetlistFm,
            RefreshPhantasyTourArtist => UpstreamProvider.PhantasyTour,
            RefreshJerryArtist => UpstreamProvider.JerryGarcia,
            RefreshLocalArtist => UpstreamProvider.Local,
            RefreshFilteredArtist r => ValidateYear(r.Year, null),
            RefreshArtist => null,
            _ => throw new InvalidImportRequest("Unknown artist refresh operation."),
        };

        return StartArtist(
            request.Artist,
            provider,
            (ArtistRefreshWorkflow workflow) => workflow.RunAsync(request, null)
        );
    }

    public Task<ImportHandle<ArtistImportResult>> RebuildArtistCatalogAsync(
        RebuildArtistCatalog request
    ) =>
        StartArtist(
            request.Artist,
            null,
            (RebuildArtistCatalogWorkflow workflow) => workflow.RunAsync(request, null)
        );

    public Task<ImportHandle<ImportBatchResult>> RefreshCatalogAsync(RefreshCatalog request) =>
        StartBatch(request);

    public Task<ImportHandle<ImportBatchResult>> RefreshRecentlyActiveArtistsAsync(
        RefreshRecentlyActiveArtists request
    ) => StartBatch(request);

    public Task<ImportHandle<ImportBatchResult>> RebuildCatalogAsync(RebuildCatalog request) =>
        StartBatch(request);

    public async Task<ImportHandle<int>> DiscoverArchiveArtistsAsync(DiscoverArchiveArtists request)
    {
        var handle = await client.StartWorkflowAsync(
            (ArchiveArtistDiscoveryWorkflow workflow) => workflow.RunAsync(request, null),
            Options("ingest/discover/" + Guid.NewGuid())
        );

        return new ImportHandle<int>(handle);
    }

    private async Task<ImportHandle<ImportBatchResult>> StartBatch(CatalogBatchCommand request)
    {
        var name = request switch
        {
            RefreshCatalog => "full",
            RefreshRecentlyActiveArtists => "thin",
            RebuildCatalog => "rebuild",
            _ => throw new InvalidImportRequest("Unknown catalog operation."),
        };

        var handle = await client.StartWorkflowAsync(
            (RefreshBatchWorkflow workflow) => workflow.RunAsync(request, null, null),
            Options("ingest/" + name + "/" + Guid.NewGuid())
        );

        return new ImportHandle<ImportBatchResult>(handle);
    }

    private async Task<ImportHandle<ArtistImportResult>> StartArtist<TWorkflow>(
        ArtistId artist,
        UpstreamProvider? provider,
        Expression<Func<TWorkflow, Task<ArtistImportResult>>> run
    )
    {
        if (artist.Value == Guid.Empty)
        {
            throw new InvalidImportRequest("Artist UUID must not be empty.");
        }

        var providers =
            await catalog.ProvidersForArtistAsync(artist) ?? throw new ArtistNotFound(artist);
        if (provider.HasValue && !providers.Contains(provider.Value))
        {
            throw new UnsupportedImportOperation(
                $"Artist has no enabled {provider.Value.ToValue()} mapping."
            );
        }

        // Temporal's atomic start, rather than a status read, owns the overlap race.
        var workflowId = "ingest/artist/" + artist;
        try
        {
            return new ImportHandle<ArtistImportResult>(
                await client.StartWorkflowAsync(run, Options(workflowId))
            );
        }
        catch (WorkflowAlreadyStartedException)
        {
            throw new ImportAlreadyRunning(workflowId);
        }
    }

    private static WorkflowOptions Options(string id) =>
        new(id, IngestionQueues.Workflows)
        {
            ExecutionTimeout = TimeSpan.FromDays(7),
            IdConflictPolicy = WorkflowIdConflictPolicy.Fail,
            IdReusePolicy = WorkflowIdReusePolicy.AllowDuplicate,
        };

    private static UpstreamProvider? ValidateYear(int? year, UpstreamProvider? provider)
    {
        if (year is < 1 or > 9999)
        {
            throw new InvalidImportRequest("Year must be between 1 and 9999.");
        }

        return provider;
    }

    /// <summary>Creates existing UTC cadences; registration never unpauses or replaces a saved schedule.</summary>
    public async Task RegisterSchedulesAsync()
    {
        foreach (
            var (name, cron) in new[]
            {
                ("discover", "30 6 * * *"),
                ("full", "0 7 * * *"),
                ("thin", "0 0,2,4,12,14,16,18,20,22 * * *"),
            }
        )
        {
            var schedule = new Schedule(
                ScheduledAction(name),
                new ScheduleSpec { CronExpressions = new[] { cron }, TimeZoneName = "UTC" }
            )
            {
                Policy = new SchedulePolicy { Overlap = ScheduleOverlapPolicy.Skip },
            };

            try
            {
                await client.CreateScheduleAsync("relisten-ingest-" + name, schedule);
            }
            catch (ScheduleAlreadyRunningException)
            {
                // Operators own existing schedules, including their paused state.
            }
        }
    }

    private static ScheduleActionStartWorkflow ScheduledAction(string name)
    {
        var options = Options("ingest/scheduled/" + name);
        if (name == "discover")
        {
            return ScheduleActionStartWorkflow.Create(
                (ArchiveArtistDiscoveryWorkflow workflow) =>
                    workflow.RunAsync(new DiscoverArchiveArtists(), null),
                options
            );
        }

        CatalogBatchCommand request =
            name == "full" ? new RefreshCatalog() : new RefreshRecentlyActiveArtists();

        return ScheduleActionStartWorkflow.Create(
            (RefreshBatchWorkflow workflow) => workflow.RunAsync(request, null, null),
            options
        );
    }

    /// <summary>Only Archive exposes a targeted import; unsupported providers have no successful no-op method.</summary>
    public sealed class ArchiveImportClient(TemporalIngestionClient owner)
    {
        public Task<ImportHandle<ArtistImportResult>> RefreshAsync(RefreshArchiveArtist request) =>
            owner.RefreshAsync(request);

        public Task<ImportHandle<ArtistImportResult>> ImportItemAsync(ImportArchiveItem request)
        {
            if (string.IsNullOrWhiteSpace(request.Item.Value))
            {
                throw new InvalidImportRequest("Archive item identifier must not be empty.");
            }

            return owner.StartArtist(
                request.Artist,
                UpstreamProvider.ArchiveOrg,
                (ArchiveItemImportWorkflow workflow) => workflow.RunAsync(request, null)
            );
        }
    }

    public sealed class JerryImportClient(TemporalIngestionClient owner)
    {
        public Task<ImportHandle<ArtistImportResult>> RefreshAsync(RefreshJerryArtist request) =>
            owner.RefreshAsync(request);

        public Task<ImportHandle<ArtistImportResult>> RepairVenuesAsync(
            RepairJerryVenues request
        ) =>
            owner.StartArtist(
                request.Artist,
                UpstreamProvider.JerryGarcia,
                (JerryVenueRepairWorkflow workflow) => workflow.RunAsync(request, null)
            );
    }

    /// <summary>Offers a provider's typed refresh request without inventing optional lifecycle methods.</summary>
    public sealed class ProviderRefreshClient<TRequest>(
        Func<TRequest, Task<ImportHandle<ArtistImportResult>>> start
    )
        where TRequest : ArtistRefreshCommand
    {
        public Task<ImportHandle<ArtistImportResult>> RefreshAsync(TRequest request) =>
            start(request);
    }
}
