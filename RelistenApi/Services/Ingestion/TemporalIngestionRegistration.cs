using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Relisten.Api.Models;
using Relisten.Data;
using Relisten.Ingestion.Providers.Archive;
using Relisten.Ingestion.Providers.ArchiveCollections;
using Relisten.Ingestion.Providers.Jerry;
using Relisten.Ingestion.Providers.Local;
using Relisten.Ingestion.Providers.PhantasyTour;
using Relisten.Ingestion.Providers.PhishIn;
using Relisten.Ingestion.Providers.PhishNet;
using Relisten.Ingestion.Providers.SetlistFm;
using Relisten.Ingestion.Providers.TapersSection;
using Relisten.Ingestion.Transport;
using Relisten.Ingestion.Workflows;
using Temporalio.Converters;
using Temporalio.Extensions.Hosting;

namespace Relisten.Ingestion;

/// <summary>Registers the shared client and independent queue workers using native Temporal controls.</summary>
public static class TemporalIngestionRegistration
{
    /// <summary>
    /// Registers a lazy Temporal client without starting workers. Temporal:PayloadRedisUrl
    /// selects a dedicated payload store; when unset, payloads use the shared RedisService.
    /// </summary>
    public static IServiceCollection AddTemporalIngestionClient(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        var temporalNamespace = configuration["Temporal:Namespace"] ?? "default";
        var payloadRedisUrl = configuration["Temporal:PayloadRedisUrl"];
        services.AddSingleton(provider =>
            string.IsNullOrWhiteSpace(payloadRedisUrl)
                ? new TemporalPayloadRedis(provider.GetRequiredService<RedisService>().db)
                : new TemporalPayloadRedis(payloadRedisUrl)
        );

        services
            .AddTemporalClient(
                configuration["Temporal:Address"] ?? "localhost:7233",
                temporalNamespace
            )
            .Configure<TemporalPayloadRedis>(
                (options, redis) =>
                    options.DataConverter = DataConverter.Default with
                    {
                        PayloadCodec = new RedisPayloadCodec(redis.Database, temporalNamespace),
                    }
            );
        services.AddSingleton<IImportAdmissionCatalog, ImportAdmissionCatalog>();
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
        services.AddSingleton<ProviderActivityRunner>();
        services.AddSingleton<IngestionHttpClientFactory>();
        // IngestionHttpHandler logs paths without query parameters that may contain credentials.
        services.AddHttpClient(nameof(IngestionHttpClientFactory)).RemoveAllLoggers();
        services
            .AddHostedTemporalWorker(IngestionQueues.Workflows)
            .AddWorkflow<ArtistRefreshWorkflow>()
            .AddWorkflow<ArchiveItemImportWorkflow>()
            .AddWorkflow<RebuildArtistCatalogWorkflow>()
            .AddWorkflow<JerryVenueRepairWorkflow>()
            .AddWorkflow<ArchiveWorkflow>()
            .AddWorkflow<PhishInWorkflow>()
            .AddWorkflow<PhishNetWorkflow>()
            .AddWorkflow<SetlistFmWorkflow>()
            .AddWorkflow<PhantasyTourWorkflow>()
            .AddWorkflow<JerryWorkflow>()
            .AddWorkflow<JerryVenueRepairProviderWorkflow>()
            .AddWorkflow<LocalWorkflow>()
            .AddWorkflow<RefreshBatchWorkflow>()
            .AddWorkflow<ArchiveArtistDiscoveryWorkflow>()
            .AddWorkflow<TapersSectionDiscoveryWorkflow>()
            .AddWorkflow<TapersSectionRefreshWorkflow>()
            .AddWorkflow<TapersSectionArtistWorkflow>()
            .AddWorkflow<ArchiveCollectionDiscoveryWorkflow>();
        services
            .AddHostedTemporalWorker(IngestionQueues.Database)
            .AddScopedActivities<CatalogActivities>()
            .AddScopedActivities<IngestionBatchActivities>()
            .AddScopedActivities<TapersSectionActivities>()
            .AddScopedActivities<ArchiveCollectionDiscoveryActivities>()
            .ConfigureOptions(o =>
            {
                o.MaxConcurrentActivities = 10;
                o.DisableEagerActivityExecution = true;
            });
        foreach (var (provider, queue) in IngestionQueues.Providers)
        {
            var builder = services.AddHostedTemporalWorker(queue);
            switch (provider)
            {
                case UpstreamProvider.ArchiveOrg:
                    builder.AddScopedActivities<ArchiveActivities>();
                    builder
                        .AddScopedActivities<TapersSectionActivities>()
                        .AddScopedActivities<ArchiveCollectionDiscoveryActivities>();
                    break;
                case UpstreamProvider.Phishin:
                    builder.AddScopedActivities<PhishInActivities>();
                    break;
                case UpstreamProvider.PhishNet:
                    builder.AddScopedActivities<PhishNetActivities>();
                    break;
                case UpstreamProvider.SetlistFm:
                    builder.AddScopedActivities<SetlistFmActivities>();
                    break;
                case UpstreamProvider.PhantasyTour:
                    builder.AddScopedActivities<PhantasyTourActivities>();
                    break;
                case UpstreamProvider.JerryGarcia:
                    builder.AddScopedActivities<JerryActivities>();
                    break;
                case UpstreamProvider.Local:
                    builder.AddScopedActivities<LocalActivities>();
                    break;
            }
            if (provider == UpstreamProvider.ArchiveOrg)
            {
                builder.AddScopedActivities<IngestionBatchActivities>();
            }

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
