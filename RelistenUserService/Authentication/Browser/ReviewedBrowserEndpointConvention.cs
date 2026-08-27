using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace RelistenUserService.Authentication.Browser;

/// <summary>
/// Applies method-aware account authorization to every segment-safe library action.
/// Session lifecycle and CSRF actions receive the web-profile policy.
/// Routes outside these families receive no browser policy from this convention.
/// Cookie-authenticated mutations still pass through the global CSRF and Origin middleware.
/// </summary>
public static class ReviewedBrowserEndpointConvention
{
    public static IEndpointConventionBuilder RequireReviewedBrowserAuthorization(
        this IEndpointConventionBuilder endpoints)
    {
        endpoints.Add(endpoint =>
        {
            var action = endpoint.Metadata
                .OfType<ControllerActionDescriptor>()
                .SingleOrDefault();
            if (action?.AttributeRouteInfo?.Template is not { } template)
            {
                return;
            }

            var policy = PolicyForRoute(RoutePath(template));
            if (policy is null
                || endpoint.Metadata.OfType<IAuthorizeData>()
                    .Any(data => data.Policy == policy))
            {
                return;
            }

            endpoint.Metadata.Add(new AuthorizeAttribute(policy));
        });
        return endpoints;
    }

    internal static string? PolicyForRoute(PathString path)
    {
        if (BrowserRouteBoundary.IsLibraryPath(path))
        {
            return AuthenticationConstants.LibraryAccessPolicy;
        }

        if (BrowserRouteBoundary.IsSessionLifecyclePath(path)
            || BrowserRouteBoundary.IsCsrfPath(path))
        {
            return AuthenticationConstants.BrowserProfileReadPolicy;
        }

        return null;
    }

    private static PathString RoutePath(string template)
    {
        if (template.StartsWith("~/", StringComparison.Ordinal))
        {
            return new PathString(template[1..]);
        }

        return new PathString(template.StartsWith('/') ? template : $"/{template}");
    }
}
