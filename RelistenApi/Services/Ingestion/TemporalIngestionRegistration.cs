using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Relisten.Ingestion.Workflows;
using Temporalio.Extensions.Hosting;

namespace Relisten.Ingestion;

/// <summary>Registers the shared client and independent queue workers using native Temporal controls.</summary>
public static class TemporalIngestionRegistration
{
    /// <summary>Registers a lazy Temporal client without starting workers.</summary>
    public static IServiceCollection AddTemporalIngestionClient(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        services.AddTemporalClient(
            configuration["Temporal:Address"] ?? "localhost:7233",
            configuration["Temporal:Namespace"] ?? "default"
        );
        services.AddSingleton<TemporalIngestionClient>();
        return services;
    }

    /// <summary>Registers workflow, database and provider workers in this host.</summary>
    public static IServiceCollection AddTemporalIngestionWorkers(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        services.AddTemporalIngestionClient(configuration);
        services.AddScoped<ImportRunRepository>();
        services
            .AddHostedTemporalWorker(IngestionQueues.Workflows)
            .AddWorkflow<ArtistRefreshWorkflow>()
            .AddWorkflow<ReimportItemWorkflow>()
            .AddWorkflow<RefreshBatchWorkflow>()
            .AddWorkflow<ArchiveArtistDiscoveryWorkflow>();
        services
            .AddHostedTemporalWorker(IngestionQueues.Database)
            .AddScopedActivities<IngestionActivities>()
            .AddScopedActivities<IngestionBatchActivities>()
            .ConfigureOptions(o =>
            {
                o.MaxConcurrentActivities = 10;
                o.DisableEagerActivityExecution = true;
            });
        foreach (var (provider, queue) in IngestionQueues.Providers)
        {
            var builder = services
                .AddHostedTemporalWorker(queue)
                .AddScopedActivities<IngestionActivities>();
            if (provider == "archive.org")
                builder.AddScopedActivities<IngestionBatchActivities>();
            builder.ConfigureOptions(o =>
            {
                o.MaxConcurrentActivities =
                    configuration.GetValue<int?>("Ingestion:Concurrency:" + queue) ?? 10;
                o.MaxTaskQueueActivitiesPerSecond = configuration.GetValue<double?>(
                    "Ingestion:Rate:" + queue
                );
                o.DisableEagerActivityExecution = true;
            });
        }
        return services;
    }
}
