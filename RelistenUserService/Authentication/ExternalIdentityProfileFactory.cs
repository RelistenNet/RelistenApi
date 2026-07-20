using System.Security.Claims;
using OpenIddict.Abstractions;
using RelistenUserService.Identity;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace RelistenUserService.Authentication;

internal static class ExternalIdentityProfileFactory
{
    public static ExternalIdentityProfile Create(
        string provider,
        ClaimsPrincipal principal)
    {
        if (!string.Equals(
                principal.GetClaim(Claims.Private.RegistrationId),
                provider,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The external identity registration does not match its callback route.");
        }

        var subject = principal.GetClaim(Claims.Subject);
        if (string.IsNullOrWhiteSpace(subject))
        {
            throw new InvalidOperationException(
                "The external provider returned no stable subject identifier.");
        }

        var email = principal.GetClaim(Claims.Email);
        return provider switch
        {
            AuthenticationConstants.GoogleProvider => new(
                AuthenticationConstants.GoogleIssuer,
                subject,
                email,
                ParseBoolean(principal.GetClaim(Claims.EmailVerified)),
                false),
            AuthenticationConstants.AppleProvider => new(
                AuthenticationConstants.AppleIssuer,
                subject,
                email,
                ParseBoolean(principal.GetClaim(Claims.EmailVerified)),
                email is null
                    ? null
                    : email.EndsWith(
                        "@privaterelay.appleid.com",
                        StringComparison.OrdinalIgnoreCase)),
            _ => throw new InvalidOperationException(
                $"External provider '{provider}' is not allowlisted.")
        };
    }

    private static bool? ParseBoolean(string? value) =>
        bool.TryParse(value, out var result) ? result : null;
}
