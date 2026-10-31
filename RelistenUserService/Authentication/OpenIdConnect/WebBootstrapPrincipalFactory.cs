using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using RelistenUserService.Identity.Entities;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace RelistenUserService.Authentication.OpenIdConnect;

public sealed class WebBootstrapPrincipalFactory
{
    public ClaimsPrincipal Create(User user, SessionId authSsoSessionId, IEnumerable<string> scopes)
    {
        var identity = new ClaimsIdentity(
            TokenValidationParameters.DefaultAuthenticationType,
            Claims.Name,
            Claims.Role
        );
        identity.AddClaim(new Claim(Claims.Subject, user.Id.ToString()));
        identity.AddClaim(new Claim(Claims.Name, user.Username));
        identity.AddClaim(new Claim(RelistenClaims.SessionId, authSsoSessionId.ToString()));

        var principal = new ClaimsPrincipal(identity);
        principal.SetScopes(scopes);
        principal.SetDestinations(claim =>
            claim.Type switch
            {
                Claims.Subject => [Destinations.AccessToken, Destinations.IdentityToken],
                Claims.Name when principal.HasScope(Scopes.Profile) => [Destinations.IdentityToken],
                RelistenClaims.SessionId => [Destinations.IdentityToken],
                _ => [],
            }
        );
        return principal;
    }
}
