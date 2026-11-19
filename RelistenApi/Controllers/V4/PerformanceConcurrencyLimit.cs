using System;
using System.Threading.RateLimiting;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Relisten.Controllers.V4;

/// <summary>Bounds performance-query work on the primary per API instance.</summary>
public sealed class PerformanceConcurrencyLimit : IAsyncResourceFilter, IDisposable
{
    private readonly ConcurrencyLimiter limiter = new(
        new ConcurrencyLimiterOptions { PermitLimit = 4, QueueLimit = 0 }
    );

    public async Task OnResourceExecutionAsync(
        ResourceExecutingContext context,
        ResourceExecutionDelegate next
    )
    {
        using var lease = limiter.AttemptAcquire();
        if (!lease.IsAcquired)
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            context.HttpContext.Response.Headers.RetryAfter = "1";
            context.Result = new StatusCodeResult(StatusCodes.Status429TooManyRequests);
            return;
        }

        await next();
    }

    public void Dispose() => limiter.Dispose();
}
