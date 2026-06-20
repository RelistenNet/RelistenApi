using Microsoft.AspNetCore.Authentication;
using Relisten.UserApi.Auth;
using Relisten.UserApi.Configuration;
using Relisten.UserApi.Serialization;
using Relisten.UserApi.Services;

namespace Relisten.UserApi;

public static class UserApiApplication
{
    public static IServiceCollection AddRelistenUserApi(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<IAuthenticatedUserContext, HttpAuthenticatedUserContext>();
        services.Configure<UserAuthOptions>(configuration.GetSection(UserAuthOptions.SectionName));
        services.AddSingleton<IAuthProviderVerifier, UnsupportedAuthProviderVerifier>();
        services.AddSingleton<UserApiDbService>();
        services.AddSingleton<UserDataSchemaInitializer>();
        services.AddSingleton<IUserAuthStore, PostgresUserAuthStore>();
        services.AddSingleton<OpaqueTokenService>();
        services.AddSingleton<ShortIdService>();
        services.AddSingleton<AccessTokenService>();
        services.AddSingleton<RefreshTokenService>();
        services.AddSingleton<CatalogSourceRangeService>();
        services.AddScoped<UserAuthService>();
        services.AddScoped<PlaylistService>();
        services.AddScoped<PlaylistSharingService>();
        services.AddScoped<UserLibrarySyncService>();

        services
            .AddControllers()
            .AddNewtonsoftJson(options => UserLibraryJson.Apply(options.SerializerSettings));

        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen();

        // Access tokens are issued by the user auth service. Provider verification is still closed
        // by default until the Apple/Google verifier workstream supplies real implementations.
        services
            .AddAuthentication(RelistenUserAuthenticationDefaults.Scheme)
            .AddScheme<AuthenticationSchemeOptions, AccessTokenAuthenticationHandler>(
                RelistenUserAuthenticationDefaults.Scheme,
                _ => { });

        services.AddAuthorization();

        return services;
    }

    public static WebApplication UseRelistenUserApi(this WebApplication app)
    {
        app.Services.GetRequiredService<UserDataSchemaInitializer>().Initialize().GetAwaiter().GetResult();

        app.UseSwagger(c =>
        {
            c.RouteTemplate = "api-docs/{documentName}/swagger.json";
        });
        app.UseSwaggerUI(c =>
        {
            c.RoutePrefix = "api-docs";
            c.SwaggerEndpoint("/api-docs/v1/swagger.json", "Relisten User API v1");
        });

        app.MapGet("/health", () => Results.Text("OK", "text/plain"));

        app.UseUserLibraryNoStoreHeaders();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();

        return app;
    }

    private static WebApplication UseUserLibraryNoStoreHeaders(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api/v3/library"))
            {
                context.Response.OnStarting(() =>
                {
                    // User-library responses default to no-store. A small number of tokenless
                    // public playlist reads set an explicit revision-cacheable policy first.
                    if (!context.Response.Headers.ContainsKey("Cache-Control"))
                    {
                        context.Response.Headers["Cache-Control"] = "no-store";
                        context.Response.Headers["Pragma"] = "no-cache";
                    }

                    return Task.CompletedTask;
                });
            }

            await next();
        });

        return app;
    }
}
