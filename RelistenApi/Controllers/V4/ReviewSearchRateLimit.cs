using System;
using System.Threading.RateLimiting;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Relisten.Controllers.V4;

/// <summary>The catalog still uses legacy MVC routing. Rate limit at the matched action.</summary>
public sealed class ReviewSearchRateLimit : IAsyncResourceFilter, IDisposable
{
    private readonly PartitionedRateLimiter<HttpContext> _limiter = PartitionedRateLimiter.Create<
        HttpContext,
        string
    >(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
            }
        )
    );

    public async Task OnResourceExecutionAsync(
        ResourceExecutingContext context,
        ResourceExecutionDelegate next
    )
    {
        using var lease = _limiter.AttemptAcquire(context.HttpContext);
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

    public void Dispose() => _limiter.Dispose();
}
