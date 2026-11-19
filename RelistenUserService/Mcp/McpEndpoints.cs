using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OpenIddict.Abstractions;
using RelistenUserService.Authentication.OpenIdConnect;
using RelistenUserService.Configuration;

namespace RelistenUserService.Mcp;

internal static class McpEndpoints
{
    private const string RateLimitPolicy = "mcp";
    private const int AnonymousRequestsPerMinute = 60;

    // A stateless tool call costs a few HTTP requests, and a playlist build makes a few dozen
    // calls in a burst. Five requests a second leaves several times that headroom for one
    // listener while bounding what a throwaway account can ask of the database and catalog.
    internal const int AccountRequestsPerMinute = 300;

    private const int MaximumRequestBytes = 2 * 1024 * 1024;
    internal const string MetadataPath = "/.well-known/oauth-protected-resource/mcp";

    public static void AddMcpEndpoints(
        this IServiceCollection services,
        McpConfiguration mcp,
        string? reviewSearchKey
    )
    {
        if (string.IsNullOrWhiteSpace(reviewSearchKey) || reviewSearchKey.Length < 32)
        {
            throw new InvalidOperationException(
                "Configure MCP_REVIEW_SEARCH_KEY with at least 32 characters when an MCP host is configured."
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

        services
            .AddMcpServer(options =>
                options.ServerInfo = new Implementation { Name = "relisten", Version = "1.0.0" }
            )
            .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
            .WithTools<CatalogBrowseTools>()
            .WithTools<CatalogEvidenceTools>()
            .WithTools<AccountMcpTools>()
            .WithResources<McpGuidance>()
            .WithPrompts<McpGuidance>();

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(RateLimitPolicy, context => RateLimitPartitionFor(context, mcp));
        });
    }

    /// <summary>
    /// Gives each linked account and each anonymous address its own request budget.
    /// </summary>
    /// <remarks>
    /// ChatGPT sends every listener's requests from shared addresses, so a signed-in caller is
    /// counted by account instead of by address. The account budget must still be finite: the
    /// OAuth client is public, so anyone can sign up and mint a token with the MCP audience.
    /// The subject comes from a token whose signature, issuer and expiry OpenIddict has
    /// checked. A token without a subject falls back to the anonymous budget.
    ///
    /// The limiter lives in process memory. Each replica counts separately, so the effective
    /// ceiling is the configured limit multiplied by the replica count.
    /// </remarks>
    private static RateLimitPartition<string> RateLimitPartitionFor(
        HttpContext context,
        McpConfiguration mcp
    )
    {
        var subject = context.User.GetClaim(OpenIddictConstants.Claims.Subject);
        var isLinkedAccount =
            context.User.Identity?.IsAuthenticated == true
            && context.User.HasAudience(mcp.Resource.AbsoluteUri)
            && !string.IsNullOrEmpty(subject);

        return isLinkedAccount
            ? RateLimitPartition.GetFixedWindowLimiter(
                "account:" + subject,
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = AccountRequestsPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                }
            )
            : RateLimitPartition.GetFixedWindowLimiter(
                "address:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown"),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = AnonymousRequestsPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                }
            );
    }

    public static void UseMcpEndpoints(
        this WebApplication app,
        AccountsRuntimeConfiguration runtime
    )
    {
        var mcp = runtime.Mcp!;

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
        app.MapMcp("/mcp")
            .RequireRateLimiting(RateLimitPolicy)
            // A playlist draft is the largest legitimate message, so use the playlist routes' cap.
            .WithMetadata(new RequestSizeLimitAttribute(MaximumRequestBytes));

        var metadata = new
        {
            resource = mcp.Resource.AbsoluteUri,
            authorization_servers = new[] { runtime.Issuer.AbsoluteUri },
            scopes_supported = McpOAuth.AdvertisedScopes,
            bearer_methods_supported = new[] { "header" },
        };
        app.MapGet(MetadataPath, () => Results.Json(metadata)).RequireRateLimiting(RateLimitPolicy);
        app.MapGet("/.well-known/oauth-protected-resource", () => Results.Json(metadata))
            .RequireRateLimiting(RateLimitPolicy);
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
