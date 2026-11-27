using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Relisten.Search;
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
using RelistenUserService.Search;
using RelistenUserService.Sonos;
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

        var (temporalAddress, temporalNamespace) = ReadTemporalSettings(builder);
        builder.Services.AddTemporalClient(temporalAddress, temporalNamespace);
        builder.Services.AddSingleton<PlaylistSearchNotifier>();
        builder.Services.AddMergedSearch(runtime, builder.Configuration, builder.Environment);
        builder.Services.AddScoped<AccountDeletionJobs>();
        builder.Services.AddSingleton<AccountDeletionStarter>();
        builder
            .Services.AddHttpClient<AppleTokenRevocation>()
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() =>
                new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }
            );

        if (runtime.Mcp is { } mcp)
        {
            // The SDK's trace logs contain complete JSON-RPC messages.
            builder.Logging.AddFilter("ModelContextProtocol", LogLevel.Information);
            builder.Services.AddMcpEndpoints(
                mcp,
                builder.Configuration[CatalogSearchExchange.KeySetting],
                builder.Configuration["MCP_WRITE_ID_KEY"]
            );
        }

        var sonos = SonosConfiguration.Read(builder.Configuration, runtime);
        builder.Services.AddSingleton(new SonosIntegration(sonos));
        if (runtime.IsLocalDevelopment && sonos is not null)
        {
            builder.Services.PostConfigure<Microsoft.AspNetCore.HostFiltering.HostFilteringOptions>(
                options => options.AllowedHosts = [.. options.AllowedHosts, sonos.PublicBase.Host]
            );
            builder.Services.Configure<ForwardedHeadersOptions>(options =>
                options.AllowedHosts.Add(sonos.PublicBase.Host)
            );
        }
        builder.Services.AddScoped<SonosAccountLink>();
        builder.Services.AddScoped<SonosHandoff>();
        builder.Services.AddScoped<SonosQueueRefresh>();
        builder.Services.AddSingleton<SonosWorkflowStarter>();
        builder.Services.AddScoped<SonosGroupEvents>();
        builder.Services.AddScoped<SonosPlaybackReports>();
        if (sonos is not null)
        {
            builder.Services.AddSingleton<SonosQueueRefreshActivities>();
            builder.Services.AddSingleton<SonosHouseholdActivities>();
            builder.Services.AddHostedService<SonosTemporalWorker>();

            if (builder.Configuration.GetValue<bool>("Sonos:LegacyQueueRefreshEnabled"))
            {
                builder.Services.AddHostedService<SonosQueueRefreshCutoverPoller>();
            }
        }
        builder
            .Services.AddHttpClient<SonosProtocol>(client =>
                client.Timeout = TimeSpan.FromSeconds(10)
            )
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() =>
                new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }
            );
        builder.Services.AddHttpClient(
            "SonosCatalog",
            client =>
            {
                client.BaseAddress = new Uri(runtime.Options.CatalogBaseUrl.TrimEnd('/') + "/");
                client.Timeout = TimeSpan.FromSeconds(5);
            }
        );

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
        app.UseMiddleware<OptionalNativeBearerMiddleware>();
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

        if (runtime.Mcp is { } mcp)
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

    // The client connects lazily, so a wrong default would surface only as a 503 on
    // every deletion request. Production must name its Temporal service explicitly.
    private static (string Address, string Namespace) ReadTemporalSettings(
        WebApplicationBuilder builder
    )
    {
        var address = builder.Configuration["Temporal:Address"];
        var temporalNamespace = builder.Configuration["Temporal:Namespace"];
        if (
            builder.Environment.IsProduction()
            && (string.IsNullOrWhiteSpace(address) || string.IsNullOrWhiteSpace(temporalNamespace))
        )
        {
            throw new InvalidOperationException(
                "Temporal__Address and Temporal__Namespace are required in Production to start account deletions."
            );
        }

        return (address ?? "localhost:7233", temporalNamespace ?? "default");
    }
}
