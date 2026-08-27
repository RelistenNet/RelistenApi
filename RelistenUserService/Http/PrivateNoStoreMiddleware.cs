using RelistenUserService.Authentication;
using RelistenUserService.Authentication.Browser;

namespace RelistenUserService.Http;

public sealed class PrivateNoStoreMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        var requiresPrivateNoStore = path.StartsWithSegments("/v1")
            || BrowserRouteBoundary.IsSessionLifecyclePath(path)
            || BrowserRouteBoundary.IsCsrfPath(path)
            || path.StartsWithSegments("/auth/sso");

        if (requiresPrivateNoStore)
        {
            context.Response.OnStarting(() =>
            {
                context.Response.Headers.CacheControl = "private, no-store";
                return Task.CompletedTask;
            });
        }

        await next(context);

        if (requiresPrivateNoStore && !context.Response.HasStarted)
        {
            context.Response.Headers.CacheControl = "private, no-store";
        }
    }
}
