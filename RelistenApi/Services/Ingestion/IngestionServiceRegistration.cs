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
        services.AddScoped<NormalizationLegacyInputService>();
        services.AddScoped<NormalizationImportedCatalogService>();
        services.AddScoped<NormalizationCatalogBindingService>();
        services.AddScoped<NormalizationRecordingProposalService>();
        services.AddScoped<NormalizationPeopleResolutionStore>();
        services.AddScoped<NormalizationPeopleResolutionProcessor>();
        services.AddScoped<NormalizationCompositionResolutionStore>();
        services.AddScoped<NormalizationCompositionResolutionProcessor>();
        services.AddScoped<CompositionResolutionPolicyService>();
        services.AddScoped<NormalizationRepertoireSourceService>();
        services.AddScoped<NormalizationRepertoireResolutionStore>();
        services.AddScoped<NormalizationRepertoireResolutionProcessor>();
        services.AddScoped<RepertoireResolutionPolicyService>();
        services.AddScoped<RepertoireConsumerPolicyService>();
        services.AddScoped<NormalizationSetlistMaterializationStore>();
        services.AddScoped<NormalizationSetlistMaterializer>();
        services.AddScoped<NormalizationRecordingContentMaterializationStore>();
        services.AddScoped<NormalizationRecordingContentMaterializer>();
        services.AddScoped<NormalizationVenueResolutionStore>();
        services.AddScoped<NormalizationVenueResolutionProcessor>();
        services.AddScoped<NormalizationVenueSourceService>();
        services.AddScoped<VenueResolutionPolicyService>();
        services.AddScoped<PeopleAcceptanceApplicationStore>();
        services.AddScoped<PeopleResolutionPolicyService>();
        services.AddScoped<SourceActorResolutionStore>();
        services.AddScoped<SourceActorResolutionService>();
        services.AddSingleton<SourceActorResolutionDecisionService>();
        services.AddScoped<SourceActorReviewStore>();
        services.AddScoped<SourceActorReviewService>();
        services.AddScoped<SourceActorAliasApplicationStore>();
        services.AddScoped<SourceActorAliasApplicationService>();
        services.AddScoped<NormalizationPeopleResolutionApplier>();
        services.AddSingleton<PeopleResolutionPolicyEvaluator>();
        services.AddSingleton<NormalizationPeopleResolutionDecisionService>();
        services.AddScoped<NormalizationRecordingProcessor>();
        services.AddScoped<NormalizationSetlistProposalService>();
        services.AddScoped<NormalizationSetlistProcessor>();
        services.AddScoped<NormalizationRecordingLookupStore>();
        services.AddScoped<NormalizationRecordingLookupService>();
        services.AddScoped<NormalizationRecordingLookupDispatcher>();
        services.AddScoped<RecordingMusicDeliveryStore>();
        services.AddScoped<RecordingMusicDeliveryService>();
        services.AddScoped<NormalizationRecordingPageStore>();
        services.AddScoped<NormalizationRecordingPageService>();
        services.AddScoped<NormalizationProviderInputService>();
        services.AddScoped<NormalizationMusicBrainzInputStore>();
        services
            .AddOptions<MusicBrainzAcquisitionOptions>()
            .BindConfiguration("Normalization:MusicBrainz");
        services
            .AddHttpClient("normalization-musicbrainz")
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() =>
                new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    UseCookies = false,
                    AutomaticDecompression = System.Net.DecompressionMethods.None,
                    MaxResponseHeadersLength = 64,
                }
            );
        services.AddScoped(sp => new NormalizationMusicBrainzInputService(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("normalization-musicbrainz"),
            sp.GetRequiredService<NormalizationMusicBrainzInputStore>(),
            sp.GetRequiredService<NormalizationEvidenceService>(),
            sp.GetRequiredService<IOptions<MusicBrainzAcquisitionOptions>>().Value
        ));
        services.AddScoped<NormalizationPhishInInputService>();
        services.AddSingleton<NormalizationProviderDecoder>();
        services.AddSingleton<RetainedRecordingCreditExtractor>();
        services.AddSingleton<NormalizationRecordingExtractor>();
        services.AddSingleton<NormalizationSetlistExtractor>();
        services
            .AddOptions<NormalizationProviderCaptureOptions>()
            .BindConfiguration("Normalization:Capture");
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
        // Catalog writers construct ShowService without the API's popularity/Hangfire stack.
        // Its read methods resolve this accessor only in a host that registers popularity services.
        services.AddScoped(sp => new Lazy<PopularityService>(
            sp.GetRequiredService<PopularityService>
        ));
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

    /// <summary>
    /// Registers API popularity reads and their background jobs. The host must supply Hangfire's
    /// background job client and Redis; standalone ingestion and normalization do not use these services.
    /// </summary>
    public static IServiceCollection AddRelistenPopularityServices(this IServiceCollection services)
    {
        services.AddScoped<PopularityCacheService>();
        services.AddScoped<PopularityService>();
        services.AddScoped<PopularityJobs>();
        return services;
    }
}
