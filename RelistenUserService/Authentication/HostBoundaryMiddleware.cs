using RelistenUserService.Configuration;

namespace RelistenUserService.Authentication;

public sealed class HostBoundaryMiddleware(
    RequestDelegate next,
    AccountsRuntimeConfiguration runtime)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var expectedHost = GetExpectedHost(context.Request.Path);
        if (expectedHost is not null && !Matches(context.Request.Host, expectedHost.Value))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await next(context);
    }

    private HostString? GetExpectedHost(PathString path)
    {
        if (runtime.Issuer.IsLoopback)
        {
            return runtime.Issuer.IsDefaultPort
                ? new HostString(runtime.Issuer.Host)
                : new HostString(runtime.Issuer.Host, runtime.Issuer.Port);
        }

        if (path.StartsWithSegments("/v1"))
        {
            return new HostString(runtime.Options.AccountsHost);
        }

        if (path.StartsWithSegments("/connect")
            || path.StartsWithSegments("/.well-known")
            || path.StartsWithSegments("/development"))
        {
            return new HostString(runtime.Options.AuthHost);
        }

        return null;
    }

    private static bool Matches(HostString actual, HostString expected) =>
        string.Equals(actual.Host, expected.Host, StringComparison.OrdinalIgnoreCase)
        && (expected.Port is null or 0 || actual.Port == expected.Port);
}
