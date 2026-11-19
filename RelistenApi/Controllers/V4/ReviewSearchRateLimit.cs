using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Configuration;

namespace Relisten.Controllers.V4;

/// <summary>The catalog still uses legacy MVC routing. Rate limit at the matched action.</summary>
public sealed class ReviewSearchRateLimit : IAsyncResourceFilter, IDisposable
{
    private const string ServiceHeader = "X-Relisten-Mcp-Review-Key";
    private readonly byte[]? serviceKey;
    private readonly PartitionedRateLimiter<string> callers = PartitionedRateLimiter.Create<
        string,
        string
    >(address =>
        RateLimitPartition.GetFixedWindowLimiter(
            address,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
            }
        )
    );
    private readonly FixedWindowRateLimiter service = new(
        new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1) }
    );

    public ReviewSearchRateLimit(IConfiguration configuration)
    {
        if (configuration["MCP_REVIEW_SEARCH_KEY"] is { Length: >= 32 } key)
        {
            serviceKey = Hash(key);
        }
    }

    public async Task OnResourceExecutionAsync(
        ResourceExecutingContext context,
        ResourceExecutionDelegate next
    )
    {
        var request = context.HttpContext.Request;
        var fromService = request.Headers.TryGetValue(ServiceHeader, out var suppliedKey);
        if (
            fromService
            && (
                serviceKey is null
                || suppliedKey.Count != 1
                || !CryptographicOperations.FixedTimeEquals(
                    serviceKey,
                    Hash(suppliedKey.ToString())
                )
            )
        )
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            context.Result = new StatusCodeResult(StatusCodes.Status403Forbidden);
            return;
        }

        using var lease = fromService
            ? service.AttemptAcquire()
            : callers.AttemptAcquire(
                context.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"
            );
        if (!lease.IsAcquired)
        {
            var response = context.HttpContext.Response;
            response.Headers.RetryAfter = "60";
            response.Headers.CacheControl = "no-store";
            context.Result = new StatusCodeResult(StatusCodes.Status429TooManyRequests);
            return;
        }

        await next();
    }

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    public void Dispose()
    {
        callers.Dispose();
        service.Dispose();
    }
}
