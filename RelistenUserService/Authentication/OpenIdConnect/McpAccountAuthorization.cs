using System.Security.Claims;
using OpenIddict.Abstractions;
using RelistenUserService.Authentication.Authorization;
using RelistenUserService.Configuration;
using RelistenUserService.Identity.Entities;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace RelistenUserService.Authentication.OpenIdConnect;

public sealed class McpAccountAuthorization(
    AccountsRuntimeConfiguration runtime,
    NativeSessionAuthenticator nativeSessions,
    IOpenIddictAuthorizationManager authorizations
)
{
    public async Task<NativeSession?> AuthenticateAsync(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken
    )
    {
        if (runtime.Mcp is not { } mcp)
        {
            return null;
        }
        var session = await nativeSessions.ReadActiveSessionAsync(
            principal,
            mcp.Resource.AbsoluteUri,
            cancellationToken
        );
        return
            session?.ClientId == McpOAuth.ClientId
            && await HasGrantAsync(session, cancellationToken)
            ? session
            : null;
    }

    public async Task<bool> HasGrantAsync(
        NativeSession session,
        CancellationToken cancellationToken
    )
    {
        if (runtime.Mcp is null)
        {
            return false;
        }
        var authorization = await authorizations.FindByIdAsync(
            session.AuthorizationId.ToString("D"),
            cancellationToken
        );
        return authorization is not null
            && await authorizations.HasStatusAsync(
                authorization,
                Statuses.Valid,
                cancellationToken
            );
    }
}
