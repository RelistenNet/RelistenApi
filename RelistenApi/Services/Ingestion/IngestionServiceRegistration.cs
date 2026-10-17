using System;
using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Import;
using Relisten.Normalization;
using Relisten.Services.Indexing;
using Relisten.Services.Notifications;
using Relisten.Services.Popularity;
using Relisten.Vendor.ArchiveOrg;

namespace Relisten.Ingestion;

/// <summary>Shares catalog services between the API and ingestion host without starting web or Hangfire services.</summary>
public static class IngestionServiceRegistration
{
    /// <summary>Registers scoped catalog services. Hosts supply their own database, Redis and configuration singletons.</summary>
    public static IServiceCollection AddRelistenCatalogServices(this IServiceCollection services)
    {
        services.AddScoped<CatalogRebuildService>();
        services.AddScoped<ShowPerformanceService>();
        services.AddScoped<CollectionService>();
        services.AddScoped<CollectionDiscoveryService>();
        services.AddScoped<ArchiveItemResolutionService>();
        services.AddScoped<ArchivePerformerReviewService>();
        services.AddScoped<ArchiveIntakeService>();
        services.AddScoped<CatalogImportTransaction>();
        services.AddScoped<NormalizationEvidenceService>();
        services.AddScoped<NormalizationWorkService>();
        services.AddScoped<NormalizationCandidateService>();
        services.AddScoped<NormalizedPeopleService>();
        services.AddScoped<NormalizedVenueService>();
        services.AddScoped<NormalizedMusicService>();
        services.AddScoped<NormalizationExtractionCacheService>();
        services.AddScoped<NormalizationReadService>();
        services.AddOptions<NormalizationReadOptions>().BindConfiguration("Normalization:Reads");
        services.AddOptions<LunaCreditExtractionOptions>().BindConfiguration("Normalization:Luna");
        services.AddHttpClient("normalization-luna").RemoveAllLoggers();
        services.AddScoped(sp => new LunaRecordingCreditExtractor(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("normalization-luna"),
            sp.GetRequiredService<NormalizationExtractionCacheService>(),
            sp.GetRequiredService<IOptions<LunaCreditExtractionOptions>>().Value,
            () => Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? ""
        ));
        services.AddSingleton<RecordingCreditExtractor>();
        services.AddSingleton<TrackTitleExtractor>();
        services.AddSingleton<IdentityCandidateMatcher>();
        services.AddScoped<SetlistShowService, SetlistShowService>();
        services.AddScoped<VenueService, VenueService>();
        services.AddScoped<TourService, TourService>();
        services.AddScoped<SetlistSongService, SetlistSongService>();
        services.AddScoped<SetlistFmImporter, SetlistFmImporter>();
        services.AddScoped<PhishinImporter, PhishinImporter>();
        services.AddScoped<ShowService, ShowService>();
        services.AddScoped<ArchiveOrgImporter, ArchiveOrgImporter>();
        services.AddScoped<ArchivePerformanceAssignmentService>();
        services.AddScoped<SourceService, SourceService>();
        services.AddScoped<SourceReviewService, SourceReviewService>();
        services.AddScoped<SourceSetService, SourceSetService>();
        services.AddScoped<SourceTrackService, SourceTrackService>();
        services.AddScoped<PhishNetImporter, PhishNetImporter>();
        services.AddScoped<PhantasyTourImporter, PhantasyTourImporter>();
        services.AddScoped<LocalImporter, LocalImporter>();
        services.AddScoped<YearService, YearService>();
        services.AddScoped<EraService, EraService>();
        services.AddScoped<ImporterService, ImporterService>();
        services.AddScoped<JerryGarciaComImporter, JerryGarciaComImporter>();
        services.AddScoped<ArtistService, ArtistService>();
        services.AddScoped<UpstreamSourceService, UpstreamSourceService>();
        services.AddScoped<IUpstreamSourceLookup>(sp =>
            sp.GetRequiredService<UpstreamSourceService>()
        );
        services.AddScoped<IArchiveOrgCollectionIndexClient, ArchiveOrgCollectionIndexClient>();
        services.AddScoped<IArchiveOrgArtistIndexRepository, ArchiveOrgArtistIndexRepository>();
        services.AddScoped<ArchiveOrgArtistIndexer, ArchiveOrgArtistIndexer>();
        services.AddScoped<PopularityCacheService, PopularityCacheService>();
        services.AddScoped<PopularityService, PopularityService>();
        services.AddScoped(sp => new Lazy<PopularityService>(
            sp.GetRequiredService<PopularityService>
        ));
        services.AddScoped<PopularityJobs, PopularityJobs>();
        services.AddScoped<ScheduledService, ScheduledService>();
        services.AddScoped<SearchService, SearchService>();
        services.AddScoped<CatalogReferenceResolver, CatalogReferenceResolver>();
        services.AddScoped<LinkService, LinkService>();
        services.AddScoped<SourceTrackPlaysService, SourceTrackPlaysService>();
        services
            .AddHttpClient<IDiscordWebhookNotifier, DiscordWebhookNotifier>(client =>
                client.Timeout = TimeSpan.FromSeconds(10)
            )
            .RemoveAllLoggers();
        return services;
    }
}
