using Microsoft.AspNetCore.Antiforgery;

namespace RelistenUserService.Authentication;

public sealed class BrowserMutationProtectionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        CurrentAccountContext currentAccount,
        IAntiforgery antiforgery)
    {
        if (!AccountRequestMethod.IsMutation(context.Request.Method)
            || !context.Request.Cookies.ContainsKey(
                AuthenticationConstants.WebSessionCookie))
        {
            await next(context);
            return;
        }

        var origins = context.Request.Headers.Origin;
        var csrfHeaders = context.Request.Headers[AuthenticationConstants.CsrfHeader];
        if (!currentAccount.IsWeb
            || currentAccount.WebOrigin is null
            || origins.Count != 1
            || !string.Equals(origins[0], currentAccount.WebOrigin, StringComparison.Ordinal)
            || csrfHeaders.Count != 1
            || string.IsNullOrWhiteSpace(csrfHeaders[0])
            || !await antiforgery.IsRequestValidAsync(context))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        await next(context);
    }
}

internal static class AccountRequestMethod
{
    public static bool IsMutation(string method) =>
        !HttpMethods.IsGet(method)
        && !HttpMethods.IsHead(method)
        && !HttpMethods.IsOptions(method)
        && !HttpMethods.IsTrace(method);
}
