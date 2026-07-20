using System.Security.Claims;
using FluentAssertions;
using NUnit.Framework;
using OpenIddict.Abstractions;
using RelistenUserService.Authentication;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace RelistenUserServiceTests;

[TestFixture]
public sealed class TestExternalIdentityProfileFactory
{
    [Test]
    public void Google_uses_the_allowlisted_issuer_and_verified_email_metadata()
    {
        var profile = ExternalIdentityProfileFactory.Create(
            AuthenticationConstants.GoogleProvider,
            Principal(
                "google-subject",
                "listener@example.com",
                emailVerified: "true"));

        profile.Should().BeEquivalentTo(new
        {
            Issuer = AuthenticationConstants.GoogleIssuer,
            Subject = "google-subject",
            Email = "listener@example.com",
            EmailVerified = (bool?)true,
            EmailIsPrivateRelay = (bool?)false
        });
    }

    [Test]
    public void Apple_detects_private_relay_without_making_email_an_identity_key()
    {
        var profile = ExternalIdentityProfileFactory.Create(
            AuthenticationConstants.AppleProvider,
            Principal(
                "apple-subject",
                "listener@privaterelay.appleid.com",
                emailVerified: "true"));

        profile.Issuer.Should().Be(AuthenticationConstants.AppleIssuer);
        profile.Subject.Should().Be("apple-subject");
        profile.EmailIsPrivateRelay.Should().BeTrue();
    }

    [Test]
    public void Google_coalesces_equivalent_claims_from_id_token_and_user_info()
    {
        var principal = Principal(
            "google-subject",
            "listener@example.com",
            emailVerified: "true");
        var identity = (ClaimsIdentity)principal.Identity!;
        identity.AddClaim(new Claim(Claims.Subject, "google-subject"));
        identity.AddClaim(new Claim(Claims.Email, "LISTENER@example.com"));
        identity.AddClaim(new Claim(Claims.EmailVerified, "True"));

        var profile = ExternalIdentityProfileFactory.Create(
            AuthenticationConstants.GoogleProvider,
            principal);

        profile.Subject.Should().Be("google-subject");
        profile.Email.Should().Be("listener@example.com");
        profile.EmailVerified.Should().BeTrue();
    }

    [Test]
    public void Conflicting_subject_claims_are_rejected()
    {
        var principal = Principal(
            "google-subject",
            "listener@example.com",
            emailVerified: "true");
        ((ClaimsIdentity)principal.Identity!).AddClaim(
            new Claim(Claims.Subject, "different-google-subject"));

        var action = () => ExternalIdentityProfileFactory.Create(
            AuthenticationConstants.GoogleProvider,
            principal);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*conflicting*'sub'*");
    }

    [Test]
    public void Missing_subject_is_rejected()
    {
        var action = () => ExternalIdentityProfileFactory.Create(
            AuthenticationConstants.GoogleProvider,
            Principal(null, "listener@example.com", emailVerified: "true"));

        action.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void Callback_route_cannot_relabel_an_authenticated_provider()
    {
        var action = () => ExternalIdentityProfileFactory.Create(
            AuthenticationConstants.GoogleProvider,
            Principal("apple-subject", "listener@example.com", emailVerified: "true"));

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*registration does not match*");
    }

    private static ClaimsPrincipal Principal(
        string? subject,
        string? email,
        string? emailVerified)
    {
        var identity = new ClaimsIdentity("external");
        identity.SetClaim(Claims.Subject, subject)
            .SetClaim(Claims.Email, email)
            .SetClaim(Claims.EmailVerified, emailVerified)
            .SetClaim(
                Claims.Private.RegistrationId,
                subject?.StartsWith("apple", StringComparison.Ordinal) == true
                    ? AuthenticationConstants.AppleProvider
                    : AuthenticationConstants.GoogleProvider);
        return new(identity);
    }
}
