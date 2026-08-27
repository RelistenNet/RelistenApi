using RelistenUserService.Configuration;

namespace RelistenUserService.Authentication;

public sealed class HostBoundaryMiddleware(
    RequestDelegate next,
    AccountsRuntimeConfiguration runtime)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!MatchesExpectedHost(context.Request.Path, context.Request.Host))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await next(context);
    }

    private bool MatchesExpectedHost(PathString path, HostString actual)
    {
        if (path.StartsWithSegments("/v1"))
        {
            return Matches(actual, new HostString(runtime.Options.AccountsHost));
        }

        if (path.StartsWithSegments("/connect")
            || path.StartsWithSegments("/.well-known")
            || path.StartsWithSegments("/development")
            || path.StartsWithSegments("/auth/sso")
            || path == AuthenticationConstants.GoogleCallbackPath
            || path == AuthenticationConstants.AppleCallbackPath)
        {
            return Matches(actual, new HostString(runtime.Options.AuthHost));
        }

        if (WebRoutePrefixes.Contains(path))
        {
            return Matches(actual, new HostString(runtime.Options.AccountsHost))
                || runtime.WebOrigins
                    .Select(origin => new Uri(origin))
                    .Any(origin => Matches(actual, HostString.FromUriComponent(origin)));
        }

        return true;
    }

    private static bool Matches(HostString actual, HostString expected) =>
        string.Equals(actual.Host, expected.Host, StringComparison.OrdinalIgnoreCase)
        && EffectiveHttpsPort(actual) == EffectiveHttpsPort(expected);

    private static int EffectiveHttpsPort(HostString host) => host.Port ?? 443;
}
