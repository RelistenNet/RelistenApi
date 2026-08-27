using Microsoft.AspNetCore.Authorization;
using RelistenUserService.Identity.Entities;

namespace RelistenUserService.Authentication;

public sealed record WebCapabilityRequirement(
    IdentitySessionCapabilities Capability) : IAuthorizationRequirement;

public sealed class WebCapabilityAuthorizationHandler(
    CurrentAccountContext currentAccount)
    : AuthorizationHandler<WebCapabilityRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        WebCapabilityRequirement requirement)
    {
        if (currentAccount.IsWeb
            && currentAccount.WebCapabilities.HasFlag(requirement.Capability))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
