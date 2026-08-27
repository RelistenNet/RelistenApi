using RelistenUserService.Configuration;

namespace RelistenUserService.Authentication.Browser;

public sealed class HostBoundaryMiddleware(
    RequestDelegate next,
    AccountsRuntimeConfiguration runtime)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!MatchesExpectedHost(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await next(context);
    }

    private bool MatchesExpectedHost(HttpRequest request)
    {
        var path = request.Path;
        var actual = request.Host;
        if (BrowserRouteBoundary.IsSharedResourcePath(path))
        {
            return MatchesAccountsOrWeb(actual);
        }

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

        if (BrowserRouteBoundary.CanRelay(path))
        {
            return MatchesAccountsOrWeb(actual);
        }

        return MatchesAccountsOrWeb(actual)
            || Matches(actual, new HostString(runtime.Options.AuthHost));
    }

    private bool MatchesAccountsOrWeb(HostString actual) =>
        Matches(actual, new HostString(runtime.Options.AccountsHost))
        || runtime.WebOrigins
            .Select(origin => new Uri(origin))
            .Any(origin => Matches(actual, HostString.FromUriComponent(origin)));

    private static bool Matches(HostString actual, HostString expected) =>
        string.Equals(actual.Host, expected.Host, StringComparison.OrdinalIgnoreCase)
        && EffectiveHttpsPort(actual) == EffectiveHttpsPort(expected);

    private static int EffectiveHttpsPort(HostString host) => host.Port ?? 443;
}
