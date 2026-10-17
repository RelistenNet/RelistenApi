using System;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Relisten.Api.Models;
using Relisten.Ingestion.Providers.ArchiveCollections;
using Relisten.Ingestion.Providers.TapersSection;
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
    }

    public bool Enabled =>
        string.Equals(
            configuration["Ingestion:Engine"],
            "Temporal",
            StringComparison.OrdinalIgnoreCase
        );

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

    /// <summary>Imports one Archive item under the same artist admission rule as a refresh.</summary>
    public Task<ImportHandle<ArtistImportResult>> ImportArchiveItemAsync(ImportArchiveItem request)
    {
        if (string.IsNullOrWhiteSpace(request.Item.Value))
        {
            throw new InvalidImportRequest("Archive item identifier must not be empty.");
        }

        return StartArtist(
            request.Artist,
            UpstreamProvider.ArchiveOrg,
            (ArchiveItemImportWorkflow workflow) => workflow.RunAsync(request, null)
        );
    }

    /// <summary>Repairs Jerry venue assignments without requesting a general artist refresh.</summary>
    public Task<ImportHandle<ArtistImportResult>> RepairJerryVenuesAsync(
        RepairJerryVenues request
    ) =>
        StartArtist(
            request.Artist,
            UpstreamProvider.JerryGarcia,
            (JerryVenueRepairWorkflow workflow) => workflow.RunAsync(request, null)
        );

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

    /// <summary>Starts one exclusive identifier discovery; bounded requests report limited coverage.</summary>
    public Task<ImportHandle<TapersSectionSummary>> DiscoverTapersSectionAsync(
        DiscoverTapersSection request
    )
    {
        if (request.MaxItems is <= 0)
        {
            throw new InvalidImportRequest("Maximum items must be positive.");
        }

        return StartIntake(
            "ingest/taperssection/discovery",
            (TapersSectionDiscoveryWorkflow w) => w.RunAsync(request, null)
        );
    }

    /// <summary>Discovers one configured Archive collection and links exact existing recordings. A page limit leaves resumable work.</summary>
    public Task<ImportHandle<ArchiveCollectionDiscoverySummary>> DiscoverArchiveCollectionAsync(
        DiscoverArchiveCollection request
    )
    {
        if (request.SelectorUuid == Guid.Empty || request.MaxPages is <= 0)
        {
            throw new InvalidImportRequest("A selector UUID and positive page limit are required.");
        }

        return StartIntake(
            "ingest/collection/" + request.SelectorUuid,
            (ArchiveCollectionDiscoveryWorkflow w) => w.RunAsync(request, null)
        );
    }

    /// <summary>Starts the exclusive coordinator for approved candidates and dirty cohort artists.</summary>
    public Task<ImportHandle<TapersSectionSummary>> RefreshTapersSectionAsync(
        RefreshTapersSection request
    ) =>
        StartIntake(
            "ingest/taperssection/refresh",
            (TapersSectionRefreshWorkflow w) => w.RunAsync(request, null, null)
        );

    /// <summary>Validates durable cohort membership, without requiring an Archive collection mapping.</summary>
    public async Task<ImportHandle<TapersSectionArtistResult>> RefreshTapersSectionArtistAsync(
        RefreshTapersSectionArtist request
    )
    {
        if (request.Artist.Value == Guid.Empty)
        {
            throw new InvalidImportRequest("Artist UUID must not be empty.");
        }

        if (!await catalog.IsTapersSectionCohortArtistAsync(request.Artist))
        {
            throw new UnsupportedImportOperation(
                "Artist is not a mapped Taper's Section cohort member."
            );
        }

        return await StartIntake(
            "ingest/artist/" + request.Artist,
            (TapersSectionArtistWorkflow w) => w.RunAsync(request, null, null, null)
        );
    }

    private async Task<ImportHandle<TResult>> StartIntake<TWorkflow, TResult>(
        string workflowId,
        Expression<Func<TWorkflow, Task<TResult>>> run
    )
    {
        try
        {
            return new(await client.StartWorkflowAsync(run, Options(workflowId)));
        }
        catch (WorkflowAlreadyStartedException)
        {
            throw new ImportAlreadyRunning(workflowId);
        }
    }

    /// <summary>Creates missing intake schedules paused; existing operator state is retained.</summary>
    public async Task RegisterTapersSectionSchedulesAsync()
    {
        foreach (
            var (name, cron) in new[]
            {
                ("taperssection-discovery", "0 8 * * *"),
                ("taperssection-refresh", "0 9 * * *"),
            }
        )
        {
            var action =
                name == "taperssection-discovery"
                    ? ScheduleActionStartWorkflow.Create(
                        (TapersSectionDiscoveryWorkflow w) => w.RunAsync(new(), null),
                        Options("ingest/taperssection/discovery")
                    )
                    : ScheduleActionStartWorkflow.Create(
                        (TapersSectionRefreshWorkflow w) => w.RunAsync(new(), null, null),
                        Options("ingest/taperssection/refresh")
                    );
            var schedule = new Schedule(
                action,
                new ScheduleSpec { CronExpressions = [cron], TimeZoneName = "UTC" }
            )
            {
                Policy = new SchedulePolicy { Overlap = ScheduleOverlapPolicy.Skip },
                State = new ScheduleState
                {
                    Paused = true,
                    Note = "Approve pilot artists and verify discovery before enabling intake.",
                },
            };
            try
            {
                await client.CreateScheduleAsync("relisten-ingest-" + name, schedule);
            }
            catch (ScheduleAlreadyRunningException)
            {
                // Existing schedules, including pause state, belong to the operator.
            }
        }
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

    // Leave IdConflictPolicy at the SDK default: overlapping starts fail,
    // and schedule serialization rejects an explicit policy.
    private static WorkflowOptions Options(string id) =>
        new(id, IngestionQueues.Workflows)
        {
            ExecutionTimeout = TimeSpan.FromDays(7),
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
}
