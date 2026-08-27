using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace RelistenUserService.Authentication;

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
