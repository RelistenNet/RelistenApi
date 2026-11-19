using RelistenUserService.Authentication;
using RelistenUserService.Authentication.Browser;

namespace RelistenUserService.Http;

[AttributeUsage(AttributeTargets.Method)]
internal sealed class PublicPlaylistCacheAttribute : Attribute;

public sealed class PrivateNoStoreMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        var requiresPrivateNoStore =
            path.StartsWithSegments("/v1")
            || path == "/mcp"
            || BrowserRouteBoundary.IsSessionLifecyclePath(path)
            || BrowserRouteBoundary.IsCsrfPath(path)
            || path.StartsWithSegments("/auth/sso");

        if (requiresPrivateNoStore)
        {
            context.Response.OnStarting(() =>
            {
                SetCacheHeaders(context);
                return Task.CompletedTask;
            });
        }

        await next(context);

        if (requiresPrivateNoStore && !context.Response.HasStarted)
        {
            SetCacheHeaders(context);
        }
    }

    private static void SetCacheHeaders(HttpContext context)
    {
        var request = context.Request;
        var headers = context.Response.Headers;
        if (
            context.Response.StatusCode == StatusCodes.Status200OK
            && HttpMethods.IsGet(request.Method)
            && context.GetEndpoint()?.Metadata.GetMetadata<PublicPlaylistCacheAttribute>()
                is not null
            && context.User.Identity?.IsAuthenticated != true
            && !request.Headers.ContainsKey("Authorization")
            && !request.Headers.ContainsKey("Cookie")
            && !headers.ContainsKey("Set-Cookie")
        )
        {
            headers.CacheControl = "no-cache";
            headers["CDN-Cache-Control"] = "max-age=60";
            return;
        }

        headers.CacheControl = "private, no-store";
        headers.Remove("CDN-Cache-Control");
    }
}
