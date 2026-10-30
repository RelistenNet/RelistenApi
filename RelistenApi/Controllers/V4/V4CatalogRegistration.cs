using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Relisten.Catalog;
using Relisten.Identifiers;
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

    public static IServiceCollection AddV4Catalog(this IServiceCollection services)
    {
        services.AddTransient<CatalogReader>();
        services.AddTransient<CatalogSongMetadataReader>();
        services.AddTransient<CatalogTrackNormalizationReader>();
        services.AddTransient<CatalogSongMatchReader>();
        services.AddTransient<CatalogSongReader>();
        services.AddTransient<CatalogBrowseReader>();
        services.AddTransient<CatalogImportedVenueMetadataReader>();
        services.AddTransient<CatalogShowMetadataReader>();
        services.AddTransient<CatalogVenueMetadataReader>();
        services.AddTransient<CatalogArtistVenueMetadataReader>();
        services.AddTransient<CatalogVenueReader>();
        services.AddCors(options =>
            options.AddPolicy(
                CorsPolicy,
                policy =>
                    policy
                        .AllowAnyOrigin()
                        .WithMethods("GET")
                        .WithHeaders("If-None-Match")
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
        options.SchemaFilter<NullableCatalogSchemaFilter>();
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
