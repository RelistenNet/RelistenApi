using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RelistenUserService.Authentication;
using RelistenUserService.Authentication.Authorization;
using RelistenUserService.Authentication.Browser;
using RelistenUserService.Authentication.Development;
using RelistenUserService.Authentication.OpenIdConnect;
using RelistenUserService.Configuration;
using RelistenUserService.Http;
using RelistenUserService.Identity.Deletion;
using RelistenUserService.Mcp;
using RelistenUserService.Persistence;
using RelistenUserService.Playlists;
using Temporalio.Extensions.Hosting;

namespace RelistenUserService;

/// <summary>Builds and initializes the shared accounts service pipeline.</summary>
public static class AccountsApplication
{
    /// <summary>Configures the normal service without starting it or opening a listener.</summary>
    public static WebApplication Build(WebApplicationBuilder builder)
    {
        var accountsOptions =
            builder.Configuration.GetSection(AccountsOptions.SectionName).Get<AccountsOptions>()
            ?? throw new InvalidOperationException(
                "The Accounts configuration section is required."
            );
        var runtime = AccountsRuntimeConfiguration.Create(accountsOptions, builder.Environment);

        builder.Services.AddProblemDetails();
        builder
            .Services.AddControllers()
            .AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.PropertyNamingPolicy =
                    JsonNamingPolicy.SnakeCaseLower;
                options.JsonSerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower;
                options.JsonSerializerOptions.UnmappedMemberHandling =
                    JsonUnmappedMemberHandling.Disallow;
            });
        builder.Services.Configure<ApiBehaviorOptions>(AccountApiBehavior.Configure);
        builder
            .Services.AddOptions<MvcOptions>()
            .Configure<ILogger<Microsoft.AspNetCore.Mvc.Formatters.SystemTextJsonInputFormatter>>(
                (options, logger) =>
                    options.InputFormatters.Insert(0, new PlaylistInputFormatter(logger))
            );
        builder.Services.AddRelistenAccounts(builder.Configuration, builder.Environment, runtime);

        if (!string.IsNullOrWhiteSpace(runtime.Options.PreviewOriginsFile))
        {
            // The control listener is never routed by public ingress. NetworkPolicy
            // admits only the trusted preview gateway to port 8082.
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.ListenAnyIP(8080);
                options.ListenAnyIP(PreviewControlMiddleware.Port);
            });
        }

        builder.Services.AddTemporalClient(
            builder.Configuration["Temporal:Address"] ?? "localhost:7233",
            builder.Configuration["Temporal:Namespace"] ?? "default"
        );
        builder.Services.AddScoped<AccountDeletionJobs>();
        builder.Services.AddSingleton<AccountDeletionStarter>();

        if (runtime.Mcp is { } mcp)
        {
            // The SDK's trace logs contain complete JSON-RPC messages.
            builder.Logging.AddFilter("ModelContextProtocol", LogLevel.Information);
            builder.Services.AddMcpEndpoints(mcp);
        }

        var app = builder.Build();
        app.UseMiddleware<PreviewControlMiddleware>();
        // TLS terminates at the cluster ingress. Only configured ingress networks may tell
        // OpenIddict that the original request was HTTPS or select a public Relisten host.
        app.UseForwardedHeaders();
        app.UseMiddleware<PrivateNoStoreMiddleware>();
        app.UseExceptionHandler(
            new ExceptionHandlerOptions
            {
                // Kestrel raises the body limit while MVC reads JSON. Preserve its client error
                // instead of reporting a transient server failure that clients might retry.
                StatusCodeSelector = exception =>
                    exception
                        is BadHttpRequestException
                        {
                            StatusCode: StatusCodes.Status413PayloadTooLarge,
                        }
                        ? StatusCodes.Status413PayloadTooLarge
                        : StatusCodes.Status500InternalServerError,
            }
        );
        app.UseMiddleware<HostBoundaryMiddleware>();
        app.UseMiddleware<WebOriginRelayMiddleware>();
        app.UseMiddleware<AccountCredentialAmbiguityMiddleware>();
        app.UseRouting();
        app.UseMiddleware<RefreshTokenReplayMiddleware>();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseMiddleware<BrowserMutationProtectionMiddleware>();

        if (runtime.Mcp is not null)
        {
            app.UseMcpEndpoints(runtime);
        }

        if (runtime.Options.EnableDevelopmentPersonas)
        {
            app.MapDevelopmentPersonaEndpoints();
        }

        app.MapControllers().RequireReviewedBrowserAuthorization();
        app.MapGet("/health/live", () => Results.Ok(new { status = "ok" }));
        app.MapGet(
            "/health/ready",
            async (AccountsDbContext dbContext, CancellationToken cancellationToken) =>
                await dbContext.Database.CanConnectAsync(cancellationToken)
                    ? Results.Ok(new { status = "ready" })
                    : Results.StatusCode(StatusCodes.Status503ServiceUnavailable)
        );
        return app;
    }

    /// <summary>Prepares persistence and validates fixed OAuth clients before serving requests.</summary>
    public static async Task InitializeAsync(
        WebApplication app,
        CancellationToken cancellationToken
    )
    {
        var runtime = app.Services.GetRequiredService<AccountsRuntimeConfiguration>();
        if (runtime.Options.ApplyMigrationsOnStartup)
        {
            // The first production rollout runs one no-surge replica, so schema installation
            // completes before the pod accepts traffic. Move this back to an operator Job before
            // scaling the service beyond one replica.
            await app
                .Services.GetRequiredService<AccountsDatabaseMigrator>()
                .MigrateAsync(cancellationToken);
        }

        await app
            .Services.GetRequiredService<WebClientApplicationInitializer>()
            .InitializeAsync(cancellationToken);

        if (runtime.Mcp is { HasAccountTools: true } mcp)
        {
            await McpOAuth.InitializeAsync(app.Services, mcp, cancellationToken);
        }

        if (runtime.IsLocalDevelopment)
        {
            // Finish local client setup before Data Protection's hosted service tries to
            // load its database-backed key ring during app startup.
            await app
                .Services.GetRequiredService<DevelopmentDatabaseInitializer>()
                .InitializeAsync(cancellationToken);
        }
    }
}
