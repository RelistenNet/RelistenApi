using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace RelistenUserService.Authentication;

public sealed class BrowserMutationProtectionFilter(
    CurrentAccountContext currentAccount,
    IAntiforgery antiforgery)
    : IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var origins = context.HttpContext.Request.Headers.Origin;
        var csrfHeaders = context.HttpContext.Request.Headers[AuthenticationConstants.CsrfHeader];
        if (!currentAccount.IsWeb
            || currentAccount.WebOrigin is null
            || origins.Count != 1
            || !string.Equals(
                origins[0],
                currentAccount.WebOrigin,
                StringComparison.Ordinal)
            || csrfHeaders.Count != 1
            || string.IsNullOrWhiteSpace(csrfHeaders[0])
            || !await antiforgery.IsRequestValidAsync(context.HttpContext))
        {
            context.Result = new StatusCodeResult(StatusCodes.Status403Forbidden);
        }
    }
}
