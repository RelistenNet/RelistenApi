using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OpenIddict.Abstractions;
using RelistenUserService.Authentication;
using RelistenUserService.Configuration;

namespace RelistenUserService.Mcp;

internal static class McpEndpoints
{
    private const string AnonymousPolicy = "mcp-anonymous";
    internal const string MetadataPath = "/.well-known/oauth-protected-resource/mcp";

    public static void AddMcpEndpoints(
        this IServiceCollection services,
        McpConfiguration mcp,
        string? reviewSearchKey
    )
    {
        if (
            mcp.HasAccountTools
            && (string.IsNullOrWhiteSpace(reviewSearchKey) || reviewSearchKey.Length < 32)
        )
        {
            throw new InvalidOperationException(
                "Configure MCP_REVIEW_SEARCH_KEY with at least 32 characters before enabling MCP account tools."
            );
        }

        services.AddHttpContextAccessor();
        services.AddSingleton(_ => new McpReviewSearchBudget(reviewSearchKey));
        services.AddScoped<McpToolAuthorization>();
        services
            .AddHttpClient<McpCatalogClient>(client => client.BaseAddress = mcp.Catalog)
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() =>
                new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }
            );

        var server = services
            .AddMcpServer(options =>
                options.ServerInfo = new Implementation { Name = "relisten", Version = "1.0.0" }
            )
            .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
            .WithTools<CatalogBrowseTools>()
            .WithTools<CatalogEvidenceTools>()
            .WithResources<McpGuidance>()
            .WithPrompts<McpGuidance>();

        if (mcp.HasAccountTools)
        {
            server.WithTools<AccountMcpTools>();
        }

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(
                AnonymousPolicy,
                context =>
                    context.User.HasAudience(mcp.Resource.AbsoluteUri)
                        ? RateLimitPartition.GetNoLimiter("mcp-bearer")
                        : RateLimitPartition.GetFixedWindowLimiter(
                            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                            _ => new FixedWindowRateLimiterOptions
                            {
                                PermitLimit = 60,
                                Window = TimeSpan.FromMinutes(1),
                            }
                        )
            );
        });
    }

    public static void UseMcpEndpoints(
        this WebApplication app,
        AccountsRuntimeConfiguration runtime
    )
    {
        var mcp = runtime.Mcp!;
        if (mcp.HasAccountTools)
        {
            AdvertiseOptionalLibraryFilter(app.Services);
        }

        app.Use(
            async (context, next) =>
            {
                if (context.Request.Path == "/mcp" && !ValidateRequest(context, mcp))
                {
                    return;
                }
                await next(context);
            }
        );
        app.UseRateLimiter();
        app.MapMcp("/mcp").RequireRateLimiting(AnonymousPolicy);

        var metadata = new
        {
            resource = mcp.Resource.AbsoluteUri,
            authorization_servers = new[] { runtime.Issuer.AbsoluteUri },
            scopes_supported = mcp.HasAccountTools
                ? new[] { RelistenScopes.UserRead, RelistenScopes.LibraryRead }
                : Array.Empty<string>(),
            bearer_methods_supported = new[] { "header" },
        };
        app.MapGet(MetadataPath, () => Results.Json(metadata)).RequireRateLimiting(AnonymousPolicy);
        app.MapGet("/.well-known/oauth-protected-resource", () => Results.Json(metadata))
            .RequireRateLimiting(AnonymousPolicy);
    }

    private static void AdvertiseOptionalLibraryFilter(IServiceProvider services)
    {
        // The SDK registers each tool as a singleton. Set optional auth metadata
        // before startup so every tools/list response reads the same descriptor.
        foreach (
            var tool in services
                .GetServices<McpServerTool>()
                .Where(tool =>
                    tool.ProtocolTool.Name
                        is "list_performances"
                            or "sample_shows"
                            or "search_reviews"
                )
        )
        {
            var scope =
                tool.ProtocolTool.Name == "search_reviews"
                    ? RelistenScopes.UserRead
                    : RelistenScopes.LibraryRead;
            tool.ProtocolTool.Meta!["securitySchemes"] = JsonSerializer.SerializeToNode(
                new object[]
                {
                    new { type = "noauth" },
                    new { type = "oauth2", scopes = new[] { scope } },
                }
            );
        }
    }

    private static bool ValidateRequest(HttpContext context, McpConfiguration mcp)
    {
        var request = context.Request;
        if (
            request.Headers.TryGetValue("Origin", out var origin)
            && origin != mcp.Resource.GetLeftPart(UriPartial.Authority)
        )
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return false;
        }
        if (request.Query.ContainsKey("access_token") || request.HasFormContentType)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return false;
        }
        if (!request.Headers.ContainsKey("Authorization"))
        {
            return true;
        }

        // OpenIddict already checked the signature, issuer, and expiry. Require
        // the MCP audience even when the caller is invoking a public tool.
        if (
            context.User.Identity?.IsAuthenticated == true
            && context.User.HasAudience(mcp.Resource.AbsoluteUri)
        )
        {
            return true;
        }

        context.Response.Headers.WWWAuthenticate = McpToolAuthorization.Challenge(
            mcp,
            "invalid_token",
            "Provide a valid Relisten MCP access token."
        );
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return false;
    }
}
