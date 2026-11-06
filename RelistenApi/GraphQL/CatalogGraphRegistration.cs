using System;
using System.Threading;
using System.Threading.Tasks;
using ChilliCream.Nitro.App;
using HotChocolate;
using HotChocolate.AspNetCore;
using HotChocolate.Execution;
using HotChocolate.Types;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Relisten.Catalog;
using Relisten.Identifiers;
using Relisten.Normalization;
using Relisten.Services.Popularity;

namespace Relisten.GraphQL;

/// <summary>Additive GraphQL transport. Request-scoped resolver services intentionally share
/// the refresh collector, while each DataLoader fetch opens its own bounded read session.</summary>
public static class CatalogGraphRegistration
{
    public const string Path = "/api/v4/graphql";
    public const string AdminPath = "/relisten-admin/graphql";
    private static readonly object RefreshKey = new();
    private static readonly object OperationKey = new();

    public static HotChocolate.Execution.Configuration.IRequestExecutorBuilder AddCatalogGraph(
        this IServiceCollection services,
        IConfiguration? configuration = null
    )
    {
        var options = new CatalogGraphOptions();
        configuration?.GetSection("GraphQL").Bind(options);
        options.Validate();
        services.AddSingleton(options);
        services.AddHttpContextAccessor();
        // Hot Chocolate creates operation/fetch scopes independently of the HTTP
        // scope. Every graph reader must receive the collector finalized below.
        services.Replace(
            ServiceDescriptor.Scoped<PopularityRefreshRequest>(provider =>
            {
                var http = provider.GetRequiredService<IHttpContextAccessor>().HttpContext;
                if (http?.Items.TryGetValue(RefreshKey, out var value) == true)
                {
                    return ((Lazy<PopularityRefreshRequest>)value!).Value;
                }
                return ActivatorUtilities.CreateInstance<PopularityRefreshRequest>(provider);
            })
        );
        services.AddScoped<CatalogGraphOperation>(provider =>
        {
            var http = provider.GetRequiredService<IHttpContextAccessor>().HttpContext;
            if (http?.Items.TryGetValue(OperationKey, out var value) == true)
            {
                return ((Lazy<CatalogGraphOperation>)value!).Value;
            }
            return ActivatorUtilities.CreateInstance<CatalogGraphOperation>(provider);
        });
        services.AddDataLoader<CatalogEntityLoader>();
        services.AddDataLoader<CatalogPageLoader>();
        var graph = services
            .AddGraphQLServer()
            .AddQueryType<CatalogQueryType>()
            .DisableIntrospection(false)
            .AddMaxExecutionDepthRule(options.MaxDepth)
            .AddApplicationService<ILogger<CatalogGraphErrors>>()
            .AddErrorFilter<CatalogGraphErrors>()
            .ModifyOptions(o =>
                o.DefaultQueryDependencyInjectionScope = DependencyInjectionScope.Request
            )
            .ModifyRequestOptions(o =>
                o.ExecutionTimeout = TimeSpan.FromSeconds(options.ExecutionSeconds)
            )
            .ModifyCostOptions(o =>
            {
                o.MaxFieldCost = options.MaxFieldCost;
                o.MaxTypeCost = options.MaxTypeCost;
            });
        // Complete the one refresh handoff before HTTP serializes the result, not
        // after the response body has reached the client.
        graph.UseRequest(
            next =>
                async context =>
                {
                    await next(context);
                    try
                    {
                        await context
                            .RequestServices.GetRequiredService<PopularityRefreshRequest>()
                            .EnqueueStale(context.RequestAborted);
                    }
                    catch (Exception exception)
                    {
                        context
                            .RequestServices.GetRequiredService<ILoggerFactory>()
                            .CreateLogger("Relisten.GraphQL")
                            .LogWarning(
                                exception,
                                "GraphQL popularity refresh could not be scheduled"
                            );
                    }
                },
            key: "CatalogRefresh",
            before: WellKnownRequestMiddleware.OperationExecutionMiddleware
        );
        graph.AddType(
            new InputObjectType<CatalogGraphFilter>(d =>
            {
                d.Field(f => f.IncludeAutoCreated).DefaultValue(false);
                d.Field(f => f.ArtistId).Type<IdType>();
                d.Field(f => f.SongId).Type<IdType>();
                d.Field(f => f.VenueId).Type<IdType>();
            })
        );
        graph.AddType<ArtistType>();
        graph.AddType<ShowType>();
        graph.AddType<SourceType>();
        graph.AddType<SourceSetType>();
        graph.AddType<SourceTrackType>();
        graph.AddType<YearType>();
        graph.AddType<TourType>();
        graph.AddType<SongType>();
        graph.AddType<ImportedSongType>();
        graph.AddType<VenueType>();
        graph.AddType<ImportedVenueType>();
        graph.AddType<ArtistVenueType>();
        graph.AddType<SourceReviewType>();
        graph.AddType<SetlistShowType>();
        graph.AddType<TrackPartType>();
        graph.AddType<TrackNormalizationType>();
        graph.AddType<SongShowType>();
        graph.AddType<SongTrackSelectionType>();
        graph.AddType(
            new InputObjectType<CatalogGraphReference>(d =>
                d.Field(r => r.Scheme).DefaultValue(CatalogReferenceScheme.V4)
            )
        );
        graph.AddType<CatalogResolutionType>();
        graph.AddType<CatalogTargetType>();
        graph.AddType<ShowRankType>();
        graph.AddType<ArtistRankType>();
        graph.AddType<ArtistRankingGroupType>();
        graph.AddType<YearRankType>();
        graph.AddType<SongEntryType>();
        graph.AddType<CatalogSearchType>();
        graph.AddType<VenueEntryType>();
        graph.AddType<LiveShowType>();
        graph
            .BindRuntimeType<ArtistId, IdType>()
            .AddTypeConverter<ArtistId, string>(id => id.ToString());
        graph
            .BindRuntimeType<ShowId, IdType>()
            .AddTypeConverter<ShowId, string>(id => id.ToString());
        graph
            .BindRuntimeType<SourceId, IdType>()
            .AddTypeConverter<SourceId, string>(id => id.ToString());
        graph
            .BindRuntimeType<SourceSetId, IdType>()
            .AddTypeConverter<SourceSetId, string>(id => id.ToString());
        graph
            .BindRuntimeType<TrackId, IdType>()
            .AddTypeConverter<TrackId, string>(id => id.ToString());
        graph
            .BindRuntimeType<YearId, IdType>()
            .AddTypeConverter<YearId, string>(id => id.ToString());
        graph
            .BindRuntimeType<TourId, IdType>()
            .AddTypeConverter<TourId, string>(id => id.ToString());
        graph
            .BindRuntimeType<ImportedVenueId, IdType>()
            .AddTypeConverter<ImportedVenueId, string>(id => id.ToString());
        graph
            .BindRuntimeType<ImportedSongId, IdType>()
            .AddTypeConverter<ImportedSongId, string>(id => id.ToString());
        graph
            .BindRuntimeType<SetlistShowId, IdType>()
            .AddTypeConverter<SetlistShowId, string>(id => id.ToString());
        graph
            .BindRuntimeType<SourceReviewId, IdType>()
            .AddTypeConverter<SourceReviewId, string>(id => id.ToString());
        graph
            .BindRuntimeType<VenueId, IdType>()
            .AddTypeConverter<VenueId, string>(id => id.ToString());
        graph
            .BindRuntimeType<SongId, IdType>()
            .AddTypeConverter<SongId, string>(id => id.ToString());
        graph
            .BindRuntimeType<ArtistVenueId, IdType>()
            .AddTypeConverter<ArtistVenueId, string>(id => id.ToString());
        return graph;
    }

    /// <summary>Maps public catalog execution and the authenticated admin editor.
    /// The host must run ASP.NET authentication and authorization middleware first.</summary>
    public static IApplicationBuilder UseCatalogGraph(this IApplicationBuilder app)
    {
        app.UseWhen(
            context =>
                string.Equals(
                    context.Request.Path.Value?.TrimEnd('/'),
                    Path,
                    StringComparison.OrdinalIgnoreCase
                ),
            branch =>
                branch.Use(
                    async (context, next) =>
                    {
                        var limits =
                            context.RequestServices.GetRequiredService<CatalogGraphOptions>();
                        context.Response.Headers.CacheControl = "no-store";
                        if (context.Request.ContentLength > limits.MaxDocumentBytes)
                        {
                            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                            return;
                        }
                        var size =
                            context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
                        if (size is { IsReadOnly: false })
                        {
                            size.MaxRequestBodySize = limits.MaxDocumentBytes;
                        }
                        var refresh = new Lazy<PopularityRefreshRequest>(() =>
                            ActivatorUtilities.CreateInstance<PopularityRefreshRequest>(
                                context.RequestServices
                            )
                        );
                        context.Items[RefreshKey] = refresh;
                        context.Items[OperationKey] = new Lazy<CatalogGraphOperation>(() =>
                            ActivatorUtilities.CreateInstance<CatalogGraphOperation>(
                                context.RequestServices
                            )
                        );
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
                            context.RequestAborted
                        );
                        deadline.CancelAfter(TimeSpan.FromSeconds(limits.ExecutionSeconds));
                        var original = context.RequestAborted;
                        context.RequestAborted = deadline.Token;
                        try
                        {
                            await next(context);
                        }
                        finally
                        {
                            context.RequestAborted = original;
                        }
                    }
                )
        );
        app.UseEndpoints(endpoints =>
        {
            endpoints
                .MapNitroApp(AdminPath)
                .WithOptions(options =>
                {
                    options.ServeMode = ServeMode.Embedded;
                    options.GraphQLEndpoint = Path;
                    options.UseBrowserUrlAsGraphQLEndpoint = true;
                    options.Title = "Relisten catalog";
                    options.UseGet = false;
                })
                .RequireAuthorization();
            endpoints
                .MapGraphQL(Path)
                .WithOptions(options =>
                {
                    options.EnableGetRequests = false;
                    options.Batching = AllowedBatching.None;
                    options.EnableMultipartRequests = false;
                    options.Tool.Enable = false;
                });
        });
        return app;
    }
}

internal sealed class CatalogGraphErrors(ILogger<CatalogGraphErrors> logger) : IErrorFilter
{
    public IError OnError(IError error)
    {
        var code = error.Exception switch
        {
            FormatException or ArgumentException => "invalid_argument",
            CatalogRetiredResourceException => "retired",
            CatalogNormalizationUnavailableException => "normalization_unavailable",
            OperationCanceledException or TimeoutException => "catalog_timeout",
            Npgsql.NpgsqlException => "catalog_unavailable",
            _ => null,
        };
        if (code is not null)
        {
            logger.LogWarning(error.Exception, "Catalog graph field failed: {ErrorCode}", code);
        }
        return code is null
            ? error
            : error
                .WithCode(code)
                .WithMessage(
                    code switch
                    {
                        "invalid_argument" => "The catalog argument is invalid.",
                        "retired" => "The catalog identity is retired.",
                        "normalization_unavailable" =>
                            "Normalized catalog data is temporarily unavailable.",
                        "catalog_timeout" => "The catalog read exceeded its deadline.",
                        _ => "Catalog data is temporarily unavailable.",
                    }
                );
    }
}
