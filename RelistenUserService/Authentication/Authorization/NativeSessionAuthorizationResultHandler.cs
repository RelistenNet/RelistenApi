using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace RelistenUserService.Authentication.Authorization;

public sealed class NativeSessionAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _fallback = new();

    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult
    )
    {
        var currentAccount = context.RequestServices.GetRequiredService<CurrentAccountContext>();
        var nativeCredentialIsInvalid =
            policy.Requirements.OfType<NativeSessionRequirement>().Any()
            && !currentAccount.IsNative;
        var reviewedCredentialIsInvalid =
            policy.Requirements.OfType<ReviewedAccountAccessRequirement>().Any()
            && !currentAccount.IsLoaded;
        if (authorizeResult.Forbidden && (nativeCredentialIsInvalid || reviewedCredentialIsInvalid))
        {
            RejectInvalidSession(context);
            return;
        }

        await _fallback.HandleAsync(next, context, policy, authorizeResult);
    }

    internal static void RejectInvalidSession(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Bearer";
    }
}
