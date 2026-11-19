using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.OpenApi;
using Relisten.Catalog;
using Relisten.Identifiers;
using Relisten.Services.Popularity;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Relisten.Controllers.V4;

/// <summary>Registers the native catalog contract without changing legacy serialization.</summary>
public static class V4CatalogRegistration
{
    public const string CorsPolicy = "V4Catalog";

    /// <summary>Runs before legacy global CORS, which otherwise ends preflight before MVC.</summary>
    public static IApplicationBuilder UseV4CatalogCors(this IApplicationBuilder app) =>
        app.UseWhen(
            context => context.Request.Path.StartsWithSegments("/api/v4"),
            catalog => catalog.UseCors(CorsPolicy)
        );

    // Use the socket peer unless the deployment explicitly trusts the forwarding chain.
    public static void UseReviewSearchForwardedClient(
        this IApplicationBuilder app,
        IConfiguration configuration
    )
    {
        var networks =
            configuration.GetSection("Catalog:ReviewSearchTrustedProxyNetworks").Get<string[]>()
            ?? [];
        if (networks.Length == 0)
        {
            return;
        }

        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor,
            ForwardLimit = null,
        };
        foreach (var network in networks)
        {
            options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
        }

        app.UseWhen(
            context =>
                context.Request.Path.StartsWithSegments("/api/v4/artists")
                && context
                    .Request.Path.Value!.TrimEnd('/')
                    .EndsWith("/reviews/search", StringComparison.OrdinalIgnoreCase),
            review => review.UseForwardedHeaders(options)
        );
    }

    public static IServiceCollection AddV4Catalog(this IServiceCollection services)
    {
        services.AddScoped<PopularityRefreshRequest>();
        services.AddTransient<CatalogReader>();
        services.AddTransient<CatalogEntityReader>();
        services.AddTransient<CatalogCollectionReader>();
        services.AddTransient<CatalogRecordingReader>();
        services.AddTransient<CatalogLiveReader>();
        services.AddTransient<CatalogLiveSummaryReader>();
        services.TryAddScoped<Relisten.Data.SourceTrackPlaysService>();
        services.AddTransient<CatalogSourceReviewReader>();
        services.AddTransient<CatalogReviewSearch>();
        services.AddTransient<CatalogPerformances>();
        services.AddSingleton<ReviewSearchRateLimit>();
        services.AddTransient<CatalogReferenceLookup>();
        services.AddTransient<CatalogResolverHydrator>();
        services.AddTransient<CatalogResolver>();
        services.AddTransient<CatalogTourReader>();
        services.AddTransient<CatalogTourMetadataReader>();
        services.AddTransient<CatalogSongMetadataReader>();
        services.AddTransient<CatalogImportedSongMappingReader>();
        services.AddTransient<CatalogTrackNormalizationReader>();
        services.AddTransient<CatalogSongMatchReader>();
        services.AddTransient<CatalogSongReader>();
        services.AddTransient<CatalogBrowseReader>();
        services.AddTransient<CatalogSearchReader>();
        services.AddTransient<CatalogRankingReader>();
        services.AddTransient<CatalogShowDiscoveryReader>();
        services.AddTransient<CatalogImportedVenueMetadataReader>();
        services.AddTransient<CatalogShowMetadataReader>();
        services.AddTransient<CatalogPopularityMetadataReader>();
        services.AddTransient<CatalogVenueMetadataReader>();
        services.AddTransient<CatalogArtistVenueMetadataReader>();
        services.AddTransient<CatalogVenueReader>();
        services.AddCors(options =>
            options.AddPolicy(
                CorsPolicy,
                policy =>
                    policy
                        .AllowAnyOrigin()
                        .WithMethods("GET", "POST")
                        .WithHeaders("If-None-Match", "Content-Type", "GraphQL-Cost")
                        .WithExposedHeaders("ETag")
            )
        );
        services.Configure<SwaggerGenOptions>(ConfigureSwagger);
        return services;
    }

    public static void ConfigureSwagger(SwaggerGenOptions options)
    {
        options.SwaggerDoc("v4", new OpenApiInfo { Title = "Relisten catalog", Version = "v4" });
        // Legacy documents intentionally contain mixed v2/v3 actions. Only v4 is isolated.
        options.DocInclusionPredicate(
            (document, action) =>
                document == "v4" ? action.GroupName == "v4" : action.GroupName != "v4"
        );
        MapId<ArtistId>(options);
        MapId<ShowId>(options);
        MapId<SourceId>(options);
        MapId<SourceSetId>(options);
        MapId<TrackId>(options);
        MapId<YearId>(options);
        MapId<TourId>(options);
        MapId<ImportedVenueId>(options);
        MapId<ImportedSongId>(options);
        MapId<SetlistShowId>(options);
        MapId<SourceReviewId>(options);
        options.MapType<Relisten.Normalization.VenueId>(() => ShortId("venue"));
        options.MapType<Relisten.Normalization.SongId>(() => ShortId("song"));
        options.MapType<Relisten.Normalization.ArtistVenueId>(() =>
            ShortId(PublicIdPrefixes.ArtistVenue)
        );
        options.SchemaFilter<CatalogResourceSchemaFilter>();
        options.ParameterFilter<CatalogQueryParameterSchema>();
        options.SchemaFilter<CatalogResolveRequestSchema>();
        options.OperationFilter<CatalogResolveRequestSchema>();
        options.OperationFilter<CatalogHttpResponseSchema>();
    }

    private static void MapId<T>(SwaggerGenOptions options)
        where T : struct, IUuidCatalogId<T>
    {
        options.MapType<T>(() =>
            new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Pattern = "^" + T.Prefix + "_[0-7][0-9a-hjkmnp-tv-z]{25}$",
            }
        );
    }

    private static OpenApiSchema ShortId(string prefix) =>
        new() { Type = JsonSchemaType.String, Pattern = "^" + prefix + "_[0-9a-hjkmnp-tv-z]{8}$" };
}
