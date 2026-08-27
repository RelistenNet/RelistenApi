using RelistenUserService.Authentication;

namespace RelistenUserService.Http;

public sealed class PrivateNoStoreMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var requiresPrivateNoStore = BrowserRouteBoundary.IsPrivatePath(context.Request.Path)
            || context.Request.Path.StartsWithSegments("/auth/sso");

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
