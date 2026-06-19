using Microsoft.AspNetCore.Authentication;
using Relisten.UserApi.Auth;
using Relisten.UserApi.Serialization;

namespace Relisten.UserApi;

public static class UserApiApplication
{
    public static IServiceCollection AddRelistenUserApi(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<IAuthenticatedUserContext, HttpAuthenticatedUserContext>();

        services
            .AddControllers()
            .AddNewtonsoftJson(options => UserLibraryJson.Apply(options.SerializerSettings));

        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen();

        // The foundation slice has protected routes before live OAuth exists. Keep production auth
        // closed by default; endpoint tests replace this scheme with a fake principal in TestServer.
        services
            .AddAuthentication(RelistenUserAuthenticationDefaults.Scheme)
            .AddScheme<AuthenticationSchemeOptions, DisabledUserAuthenticationHandler>(
                RelistenUserAuthenticationDefaults.Scheme,
                _ => { });

        services.AddAuthorization();

        return services;
    }

    public static WebApplication UseRelistenUserApi(this WebApplication app)
    {
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
                    // User-library responses are account-scoped or security-sensitive, including
                    // auth challenges, so this namespace defaults to no-store.
                    context.Response.Headers.CacheControl = "no-store";
                    context.Response.Headers.Pragma = "no-cache";
                    return Task.CompletedTask;
                });
            }

            await next();
        });

        return app;
    }
}
