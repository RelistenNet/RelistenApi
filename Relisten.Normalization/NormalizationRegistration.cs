using Microsoft.Extensions.DependencyInjection;
using Relisten.Normalization.Songs;
using Relisten.Normalization.Temporal;
using Temporalio.Extensions.Hosting;

namespace Relisten.Normalization;

/// How a host wires normalization in. The host also registers an INormalizationDatabase.
public static class NormalizationRegistration
{
    public static IServiceCollection AddNormalization(this IServiceCollection services)
    {
        services.AddSingleton<ICatalogueModel, OpenAiCatalogueModel>();
        services.AddScoped<CatalogueLlm>();
        services.AddScoped<CatalogueMatch>();
        services.AddScoped<Venues.VenueMatch>();
        services.AddScoped<Venues.VenueLlm>();
        services.AddSingleton<NormalizationClient>();
        return services;
    }

    /// Normalization's workflows, on the host's workflow worker.
    public static ITemporalWorkerServiceOptionsBuilder AddNormalizationWorkflows(
        this ITemporalWorkerServiceOptionsBuilder workflows
    ) => workflows.AddWorkflow<SongCatalogueWorkflow>().AddWorkflow<VenueCatalogueWorkflow>();

    /// Normalization's database steps, on the host's database worker.
    public static ITemporalWorkerServiceOptionsBuilder AddNormalizationActivities(
        this ITemporalWorkerServiceOptionsBuilder database
    ) => database.AddScopedActivities<SongActivities>().AddScopedActivities<VenueActivities>();
}
