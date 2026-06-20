using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
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
        services.AddDataProtection().SetApplicationName("RelistenUserApi");
        services.AddScoped<IAuthenticatedUserContext, HttpAuthenticatedUserContext>();
        services.Configure<UserAuthOptions>(configuration.GetSection(UserAuthOptions.SectionName));
        services.AddSingleton<IOpenIdConnectConfigurationSource, OpenIdConnectConfigurationSource>();
        services.AddSingleton<IAuthProviderVerifier, OidcAuthProviderVerifier>();
        services.AddSingleton<UserApiDbService>();
        services.AddSingleton<UserDataSchemaInitializer>();
        services.AddSingleton<IUserAuthStore, PostgresUserAuthStore>();
        services.AddSingleton<OpaqueTokenService>();
        services.AddSingleton<ShortIdService>();
        services.AddSingleton<AccessTokenService>();
        services.AddSingleton<RefreshTokenService>();
        services.AddScoped<WebSessionCookieEvents>();
        services.AddScoped<WebSessionCookieService>();
        services.AddScoped<WebOAuthStateService>();
        services.AddScoped<WebOAuthService>();
        services.AddHttpClient<IWebOAuthCodeExchanger, GoogleWebOAuthCodeExchanger>();
        services.AddSingleton<CatalogSourceRangeService>();
        services.AddScoped<UserAuthService>();
        services.AddScoped<PlaylistService>();
        services.AddScoped<PlaylistSharingService>();
        services.AddScoped<UserLibrarySyncService>();
        services.AddScoped<PlaybackHistoryCatalogAggregateSink>();
        services.AddScoped<PlaybackHistoryService>();
        services.AddScoped<UserAccountDataService>();

        services
            .AddControllers()
            .AddNewtonsoftJson(options => UserLibraryJson.Apply(options.SerializerSettings));

        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen();

        // Access tokens are issued by the user auth service after Apple/Google ID tokens
        // are verified by the configured provider verifier.
        services
            .AddAuthentication(RelistenUserAuthenticationDefaults.Scheme)
            .AddPolicyScheme(
                RelistenUserAuthenticationDefaults.Scheme,
                RelistenUserAuthenticationDefaults.Scheme,
                options =>
                {
                    options.ForwardDefaultSelector = context =>
                    {
                        var authorization = context.Request.Headers.Authorization.ToString();
                        return authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                            ? RelistenUserAuthenticationDefaults.BearerScheme
                            : RelistenUserAuthenticationDefaults.WebSessionScheme;
                    };
                })
            .AddScheme<AuthenticationSchemeOptions, AccessTokenAuthenticationHandler>(
                RelistenUserAuthenticationDefaults.BearerScheme,
                _ => { })
            .AddCookie(
                RelistenUserAuthenticationDefaults.WebSessionScheme,
                _ => { });
        services
            .AddOptions<CookieAuthenticationOptions>(RelistenUserAuthenticationDefaults.WebSessionScheme)
            .Configure<IOptions<UserAuthOptions>>((cookieOptions, authOptions) =>
            {
                var web = authOptions.Value.Web;
                cookieOptions.Cookie.Name = web.SessionCookieName;
                cookieOptions.Cookie.HttpOnly = true;
                cookieOptions.Cookie.SameSite = SameSiteMode.Lax;
                cookieOptions.Cookie.SecurePolicy = web.SecureCookies
                    ? CookieSecurePolicy.Always
                    : CookieSecurePolicy.None;
                cookieOptions.Cookie.Path = "/";
                cookieOptions.EventsType = typeof(WebSessionCookieEvents);
                cookieOptions.SlidingExpiration = true;
                cookieOptions.ExpireTimeSpan = TimeSpan.FromDays(Math.Max(1, web.SessionCookieDays));
            });

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
        app.UseMiddleware<UserLibraryWebCsrfMiddleware>();
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
