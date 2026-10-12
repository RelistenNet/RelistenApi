using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Relisten.Ingestion.Workflows;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Client.Schedules;
using Temporalio.Exceptions;

namespace Relisten.Ingestion;

/// <summary>Starts Temporal ingestion without coupling API callers to worker implementation details.</summary>
public sealed class TemporalIngestionClient(ITemporalClient client, IConfiguration configuration)
{
    /// <summary>Whether API requests should use Temporal instead of Hangfire ingestion.</summary>
    public bool Enabled =>
        string.Equals(
            configuration["Ingestion:Engine"],
            "Temporal",
            StringComparison.OrdinalIgnoreCase
        );

    /// <summary>Starts an artist operation, or returns the existing active workflow ID on overlap.</summary>
    public async Task<string> StartArtistAsync(ArtistImportRequest request)
    {
        if (!Guid.TryParse(request.Artist, out var artistUuid))
        {
            throw new ArgumentException("Artist imports require a catalog UUID.", nameof(request));
        }

        request = request with { Artist = artistUuid.ToString() };
        var id = "ingest/artist/" + request.Artist;
        var options = new WorkflowOptions(id, IngestionQueues.Workflows)
        {
            ExecutionTimeout = IngestionRun.MaximumDuration,
        };
        try
        {
            if (request.Item == null)
            {
                await client.StartWorkflowAsync(
                    (ArtistRefreshWorkflow w) => w.RunAsync(request, null),
                    options
                );
            }
            else
            {
                await client.StartWorkflowAsync(
                    (ReimportItemWorkflow w) => w.RunAsync(request),
                    options
                );
            }
        }
        catch (WorkflowAlreadyStartedException)
        {
            // Preserve the current skip-overlap behavior.
        }

        return id;
    }

    /// <summary>Starts full/thin/rebuild catalog work, or Archive artist discovery.</summary>
    public async Task<string> StartBatchAsync(string mode)
    {
        if (mode is not ("discover" or "full" or "thin" or "rebuild"))
        {
            throw new ArgumentException("Expected discover, full, thin or rebuild.", nameof(mode));
        }

        var id = "ingest/" + mode + "/" + Guid.NewGuid();
        var options = new WorkflowOptions(id, IngestionQueues.Workflows)
        {
            ExecutionTimeout = IngestionRun.MaximumDuration,
        };
        if (mode == "discover")
        {
            await client.StartWorkflowAsync(
                (ArchiveArtistDiscoveryWorkflow w) => w.RunAsync(null, null, 0),
                options
            );
        }
        else
        {
            await client.StartWorkflowAsync(
                (RefreshBatchWorkflow w) => w.RunAsync(new ImportBatchRequest(mode, id)),
                options
            );
        }

        return id;
    }

    /// <summary>Creates the existing UTC ingestion cadences. Existing schedules, including paused ones, are left intact.</summary>
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
            var id = "relisten-ingest-" + name;
            var options = new WorkflowOptions("ingest/scheduled/" + name, IngestionQueues.Workflows)
            {
                ExecutionTimeout = IngestionRun.MaximumDuration,
            };
            var action =
                name == "discover"
                    ? ScheduleActionStartWorkflow.Create(
                        (ArchiveArtistDiscoveryWorkflow w) => w.RunAsync(null, null, 0),
                        options
                    )
                    : ScheduleActionStartWorkflow.Create(
                        (RefreshBatchWorkflow w) => w.RunAsync(new ImportBatchRequest(name)),
                        options
                    );
            var schedule = new Schedule(
                action,
                new ScheduleSpec { CronExpressions = new[] { cron }, TimeZoneName = "UTC" }
            )
            {
                Policy = new SchedulePolicy { Overlap = ScheduleOverlapPolicy.Skip },
            };
            try
            {
                await client.CreateScheduleAsync(id, schedule);
            }
            catch (ScheduleAlreadyRunningException) { }
        }
    }
}
