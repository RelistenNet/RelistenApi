namespace RelistenUserService.Authentication.OpenIdConnect;

/// <summary>
/// Exposes only reload acknowledgement on a transport-separated private listener.
/// Public requests cannot select this listener with Host or forwarding headers.
/// </summary>
public sealed class PreviewControlMiddleware(RequestDelegate next)
{
    public const int Port = 8082;
    public const string Path = "/__preview/auth";

    public async Task InvokeAsync(HttpContext context, PreviewOriginFileLoader loader)
    {
        if (context.Connection.LocalPort != Port)
        {
            if (context.Request.Path.StartsWithSegments("/__preview"))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            await next(context);
            return;
        }

        context.Response.Headers.CacheControl = "private, no-store";
        if (context.Request.Method != "GET" || context.Request.Path != Path)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        var status = loader.Status;
        await context.Response.WriteAsJsonAsync(
            new
            {
                appliedRevision = status.AppliedRevision,
                sourceState = status.SourceState.ToString().ToLowerInvariant(),
            }
        );
    }
}
