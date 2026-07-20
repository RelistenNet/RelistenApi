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
        var registrationId = GetUnambiguousClaim(
            principal,
            Claims.Private.RegistrationId,
            StringComparer.Ordinal);
        if (!string.Equals(
                registrationId,
                provider,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The external identity registration does not match its callback route.");
        }

        var subject = GetUnambiguousClaim(
            principal,
            Claims.Subject,
            StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(subject))
        {
            throw new InvalidOperationException(
                "The external provider returned no stable subject identifier.");
        }

        var email = GetUnambiguousClaim(
            principal,
            Claims.Email,
            StringComparer.OrdinalIgnoreCase);
        var emailVerified = ParseBoolean(GetUnambiguousClaim(
            principal,
            Claims.EmailVerified,
            StringComparer.OrdinalIgnoreCase));
        return provider switch
        {
            AuthenticationConstants.GoogleProvider => new(
                AuthenticationConstants.GoogleIssuer,
                subject,
                email,
                emailVerified,
                false),
            AuthenticationConstants.AppleProvider => new(
                AuthenticationConstants.AppleIssuer,
                subject,
                email,
                emailVerified,
                email is null
                    ? null
                    : email.EndsWith(
                        "@privaterelay.appleid.com",
                        StringComparison.OrdinalIgnoreCase)),
            _ => throw new InvalidOperationException(
                $"External provider '{provider}' is not allowlisted.")
        };
    }

    private static string? GetUnambiguousClaim(
        ClaimsPrincipal principal,
        string type,
        StringComparer comparer)
    {
        // OIDC providers may emit the same claim in both the ID token and the
        // user-info response. Equivalent duplicates are harmless; conflicting
        // identity values must fail closed instead of depending on claim order.
        var values = principal.FindAll(type)
            .Select(claim => claim.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(comparer)
            .ToArray();
        return values.Length switch
        {
            0 => null,
            1 => values[0],
            _ => throw new InvalidOperationException(
                $"The external provider returned conflicting '{type}' claims.")
        };
    }

    private static bool? ParseBoolean(string? value) =>
        bool.TryParse(value, out var result) ? result : null;
}
