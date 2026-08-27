using Microsoft.Extensions.Primitives;
using RelistenUserService.Configuration;

namespace RelistenUserService.Authentication.Browser;

/// <summary>
/// Reconstructs a browser-visible origin only for reviewed browser routes.
/// A relay hint is accepted only on an expected backend with one exact configured origin.
/// The relay header grants no authority.
/// </summary>
public sealed class WebOriginRelayMiddleware(
    RequestDelegate next,
    AccountsRuntimeConfiguration runtime)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var hasRelay = context.Request.Headers.TryGetValue(
            AuthenticationConstants.WebOriginHeader,
            out var relayedOrigins);
        if (!BrowserRouteBoundary.CanRelay(context.Request.Path))
        {
            // A relay hint on another route could make caller-supplied host data affect
            // a native or auth-host request.
            if (hasRelay)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            await next(context);
            return;
        }

        var backendOrigin = CurrentOrigin(context.Request);
        string webOrigin;
        if (hasRelay)
        {
            if (!IsExpectedBackend(backendOrigin)
                || !TryGetRelayedOrigin(relayedOrigins, out webOrigin))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
        }
        else if (runtime.WebOrigins.Contains(backendOrigin, StringComparer.Ordinal))
        {
            webOrigin = backendOrigin;
        }
        else if (BrowserRouteBoundary.IsSharedResourcePath(context.Request.Path)
            && IsAccountsBackend(backendOrigin))
        {
            await next(context);
            return;
        }
        else
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        // OpenIddict must see the browser-visible callback URI. Redirect validation would
        // fail if the local proxy's accounts host remained on the request.
        var origin = new Uri(webOrigin);
        context.Request.Scheme = Uri.UriSchemeHttps;
        context.Request.Host = HostString.FromUriComponent(origin);
        context.Request.Headers.Remove(AuthenticationConstants.WebOriginHeader);
        context.Features.Set<IWebOriginFeature>(new WebOriginFeature(webOrigin));
        await next(context);
    }

    private bool TryGetRelayedOrigin(
        StringValues values,
        out string webOrigin)
    {
        webOrigin = "";
        if (values.Count != 1)
        {
            return false;
        }

        var value = values[0];
        if (value is null
            || !runtime.WebOrigins.Contains(value, StringComparer.Ordinal))
        {
            return false;
        }

        webOrigin = value;
        return true;
    }

    private bool IsExpectedBackend(string origin) =>
        IsAccountsBackend(origin)
        || runtime.WebOrigins.Contains(origin, StringComparer.Ordinal);

    private bool IsAccountsBackend(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var actual)
        && string.Equals(actual.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && Matches(actual, new HostString(runtime.Options.AccountsHost));

    private static bool Matches(Uri actual, HostString expected) =>
        string.Equals(actual.Host, expected.Host, StringComparison.OrdinalIgnoreCase)
        && actual.Port == (expected.Port ?? 443);

    private static string CurrentOrigin(HttpRequest request) =>
        $"{request.Scheme}://{request.Host.ToUriComponent()}";
}

public interface IWebOriginFeature
{
    string Origin { get; }
}

public sealed record WebOriginFeature(string Origin) : IWebOriginFeature;
