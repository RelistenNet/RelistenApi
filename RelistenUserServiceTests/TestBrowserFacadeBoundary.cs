using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using OpenIddict.Abstractions;
using Relisten.Accounts.Contracts.Accounts;
using RelistenUserService.Authentication;
using RelistenUserService.Controllers;
using RelistenUserService.Identity.Entities;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace RelistenUserServiceTests;

[TestFixture]
public sealed class TestBrowserFacadeBoundary
{
    [Test]
    public void Browser_profile_has_no_native_session_identifier()
    {
        var properties = typeof(BrowserAccountProfileResponse)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name));

        properties.Should().NotContain("native_session_uuid");
    }

    [Test]
    public void Facade_exposes_only_the_five_reviewed_method_and_path_pairs()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllers()
            .AddApplicationPart(typeof(BrowserMeController).Assembly);
        using var provider = services.BuildServiceProvider();
        var actions = provider.GetRequiredService<IActionDescriptorCollectionProvider>()
            .ActionDescriptors.Items
            .OfType<ControllerActionDescriptor>()
            .Where(action => action.AttributeRouteInfo?.Template?
                .StartsWith("api/user/v1", StringComparison.Ordinal) == true)
            .Select(action => $"{action.ActionConstraints!.OfType<Microsoft.AspNetCore.Mvc.ActionConstraints.HttpMethodActionConstraint>().Single().HttpMethods.Single()} /{action.AttributeRouteInfo!.Template}")
            .Order()
            .ToArray();

        actions.Should().Equal(
            "GET /api/user/v1/csrf",
            "GET /api/user/v1/library/changes",
            "GET /api/user/v1/library/snapshot",
            "GET /api/user/v1/me",
            "POST /api/user/v1/library/favorite-mutations:batch");
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
}
