using Microsoft.AspNetCore.Authorization;
using OpenIddict.Abstractions;
using RelistenUserService.Identity.Entities;

namespace RelistenUserService.Authentication;

public sealed record ReviewedAccountAccessRule(
    string NativeScope,
    IdentitySessionCapabilities WebCapability);

public sealed record ReviewedAccountAccessRequirement(
    ReviewedAccountAccessRule DefaultRule,
    ReviewedAccountAccessRule? MutationRule = null) : IAuthorizationRequirement;

public sealed class ReviewedAccountAccessAuthorizationHandler(
    NativeSessionAuthenticator nativeSessions,
    CurrentAccountContext currentAccount)
    : AuthorizationHandler<ReviewedAccountAccessRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ReviewedAccountAccessRequirement requirement)
    {
        var rule = SelectRule(context.Resource, requirement);
        if (currentAccount.IsWeb)
        {
            if (currentAccount.WebCapabilities.HasFlag(rule.WebCapability))
            {
                context.Succeed(requirement);
            }
            return;
        }

        var nativeSession = await nativeSessions.AuthenticateAsync(context.User);
        if (nativeSession is null)
        {
            return;
        }

        currentAccount.SetNative(nativeSession.User, nativeSession.SessionId);
        if (context.User.HasScope(rule.NativeScope))
        {
            context.Succeed(requirement);
        }
    }

    private static ReviewedAccountAccessRule SelectRule(
        object? resource,
        ReviewedAccountAccessRequirement requirement)
    {
        if (requirement.MutationRule is null)
        {
            return requirement.DefaultRule;
        }

        return resource is HttpContext httpContext
            && !AccountRequestMethod.IsMutation(httpContext.Request.Method)
                ? requirement.DefaultRule
                : requirement.MutationRule;
    }
}
