using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Relisten.UserApi.Configuration;

namespace Relisten.UserApi.Auth;

public sealed class UserLibraryWebCsrfMiddleware
{
    private static readonly HashSet<string> SafeMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        HttpMethods.Get,
        HttpMethods.Head,
        HttpMethods.Options,
        HttpMethods.Trace
    };

    private readonly RequestDelegate _next;

    public UserLibraryWebCsrfMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task Invoke(HttpContext context, IOptions<UserAuthOptions> options)
    {
        if (!RequiresCsrfCheck(context))
        {
            await _next(context);
            return;
        }

        var web = options.Value.Web;
        if (!OriginIsAllowed(context.Request, web) || !CsrfTokenMatches(context.Request, web))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        await _next(context);
    }

    private static bool RequiresCsrfCheck(HttpContext context)
    {
        return context.Request.Path.StartsWithSegments("/api/v3/library") &&
            !SafeMethods.Contains(context.Request.Method) &&
            context.User.Identities.Any(identity =>
                identity.IsAuthenticated &&
                string.Equals(
                    identity.AuthenticationType,
                    RelistenUserAuthenticationDefaults.WebSessionScheme,
                    StringComparison.Ordinal));
    }

    private static bool OriginIsAllowed(HttpRequest request, WebSessionOptions options)
    {
        var origin = request.Headers.Origin.ToString();
        if (string.IsNullOrWhiteSpace(origin))
        {
            return false;
        }

        var allowedOrigins = options.AllowedOrigins
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim().TrimEnd('/'))
            .ToArray();
        if (allowedOrigins.Length > 0)
        {
            return allowedOrigins.Contains(origin.TrimEnd('/'), StringComparer.OrdinalIgnoreCase);
        }

        var sameOrigin = $"{request.Scheme}://{request.Host}".TrimEnd('/');
        return string.Equals(origin.TrimEnd('/'), sameOrigin, StringComparison.OrdinalIgnoreCase);
    }

    private static bool CsrfTokenMatches(HttpRequest request, WebSessionOptions options)
    {
        var cookie = request.Cookies[options.CsrfCookieName];
        var header = request.Headers[options.CsrfHeaderName].ToString();
        if (string.IsNullOrWhiteSpace(cookie) || string.IsNullOrWhiteSpace(header))
        {
            return false;
        }

        var cookieBytes = Encoding.UTF8.GetBytes(cookie);
        var headerBytes = Encoding.UTF8.GetBytes(header);
        return cookieBytes.Length == headerBytes.Length &&
            CryptographicOperations.FixedTimeEquals(cookieBytes, headerBytes);
    }
}
