using System.Globalization;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using RelistenUserService.Identity.Entities;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace RelistenUserService.Authentication;

public sealed class NativePrincipalFactory(Configuration.AccountsRuntimeConfiguration runtime)
{
    public ClaimsPrincipal Create(
        User user,
        NativeSession session,
        IEnumerable<string> scopes)
    {
        var identity = new ClaimsIdentity(
            TokenValidationParameters.DefaultAuthenticationType,
            Claims.Name,
            Claims.Role);
        identity.AddClaim(new Claim(Claims.Subject, user.Id.ToString("D")));
        identity.AddClaim(new Claim(Claims.Name, user.Username));
        identity.AddClaim(new Claim(RelistenClaims.SessionId, session.Id.ToString("D")));
        identity.AddClaim(new Claim(Claims.ClientId, session.ClientId));
        identity.AddClaim(new Claim(
            RelistenClaims.SecurityVersion,
            user.SecurityVersion.ToString(CultureInfo.InvariantCulture)));

        var principal = new ClaimsPrincipal(identity);
        principal.SetScopes(scopes);
        principal.SetResources(runtime.Options.Audience);
        principal.SetAuthorizationId(session.AuthorizationId.ToString("D"));
        principal.SetDestinations(claim => claim.Type switch
        {
            Claims.Subject => [Destinations.AccessToken, Destinations.IdentityToken],
            Claims.Name when principal.HasScope(Scopes.Profile) => [Destinations.IdentityToken],
            RelistenClaims.SessionId or Claims.ClientId or RelistenClaims.SecurityVersion =>
                [Destinations.AccessToken],
            _ => []
        });

        return principal;
    }
}
