using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NUnit.Framework;
using OpenIddict.Abstractions;
using Relisten.Accounts.Contracts.Accounts;
using RelistenUserService.Authentication;
using RelistenUserService.Authentication.Authorization;
using RelistenUserService.Authentication.OpenIdConnect;
using RelistenUserService.Controllers;
using RelistenUserService.Identity.Entities;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace RelistenUserServiceTests;

[TestFixture]
public sealed class TestBrowserFacadeBoundary
{
    [Test]
    public void Native_me_response_includes_the_native_session_uuid()
    {
        var user = User();
        var nativeSessionId = Guid.CreateVersion7();
        var currentAccount = new CurrentAccountContext();
        currentAccount.SetNative(user, nativeSessionId);

        var json = SerializeMe(currentAccount);

        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("native_session_uuid").GetGuid()
            .Should().Be(nativeSessionId);
    }

    [Test]
    public void Web_me_response_omits_the_native_session_uuid()
    {
        var currentAccount = new CurrentAccountContext();
        currentAccount.SetWeb(
            User(),
            Guid.CreateVersion7(),
            AuthenticationConstants.LocalWebOrigin,
            IdentitySessionCapabilities.AllWeb);

        var json = SerializeMe(currentAccount);

        using var document = JsonDocument.Parse(json);
        document.RootElement.TryGetProperty("native_session_uuid", out _)
            .Should().BeFalse();
    }

    [Test]
    public void Web_bootstrap_principal_has_no_native_resource_or_security_claims()
    {
        var user = new User
        {
            Id = Guid.CreateVersion7(),
            Username = "browser_user",
            SecurityVersion = 7
        };
        var authSsoSessionId = Guid.CreateVersion7();

        var principal = new WebBootstrapPrincipalFactory().Create(
            user,
            authSsoSessionId,
            [Scopes.OpenId, Scopes.Profile]);

        principal.GetResources().Should().BeEmpty();
        principal.GetClaim(RelistenClaims.SessionId).Should()
            .Be(authSsoSessionId.ToString("D"));
        principal.GetClaim(RelistenClaims.SecurityVersion).Should().BeNull();
        principal.GetClaim(Claims.ClientId).Should().BeNull();
        principal.Claims.Single(claim => claim.Type == RelistenClaims.SessionId)
            .GetDestinations()
            .Should().Equal(Destinations.IdentityToken);
    }

    private static string SerializeMe(CurrentAccountContext currentAccount)
    {
        var context = new DefaultHttpContext();
        var controller = new MeController(currentAccount, null!)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
        var result = controller.Get().Result.Should().BeOfType<OkObjectResult>().Subject;
        return JsonSerializer.Serialize(result.Value, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        });
    }

    private static User User() => new()
    {
        Id = Guid.CreateVersion7(),
        Username = "browser_user",
        SecurityVersion = 7
    };
}
