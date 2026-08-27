using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using NUnit.Framework;
using OpenIddict.Abstractions;
using OpenIddict.Client.WebIntegration;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;
using RelistenUserService.Authentication;
using RelistenUserService.Authentication.OpenIdConnect;
using RelistenUserService.Configuration;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace RelistenUserServiceTests;

[TestFixture]
public sealed class TestAuthorizationAccountSelection
{
    [TestCase(
        AuthenticationConstants.GoogleProvider,
        OpenIddictClientWebIntegrationConstants.Providers.Google,
        true)]
    [TestCase(
        AuthenticationConstants.AppleProvider,
        OpenIddictClientWebIntegrationConstants.Providers.Apple,
        false)]
    public async Task Select_account_bypasses_auth_sso_and_challenges_the_requested_provider(
        string provider,
        string authenticationScheme,
        bool forwardsPrompt)
    {
        var request = new OpenIddictRequest
        {
            ClientId = "relisten-mobile-ios",
            Prompt = PromptValues.SelectAccount
        };
        request.SetParameter("provider", provider);
        var context = new DefaultHttpContext();
        context.Request.Path = "/connect/authorize";
        context.Request.QueryString = new QueryString(
            $"?client_id=relisten-mobile-ios&provider={provider}&prompt=select_account&state=test-state");
        context.Request.Headers.Cookie = $"{AuthenticationConstants.AuthSsoCookie}=opaque-session";
        context.Features.Set(new OpenIddictServerAspNetCoreFeature
        {
            Transaction = new OpenIddictServerTransaction
            {
                Request = request
            }
        });
        var controller = new AuthorizationController(
            null!,
            Runtime(provider),
            null!,
            null!,
            null!,
            null!,
            null!,
            TimeProvider.System)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };

        var result = await controller.Authorize(CancellationToken.None);

        var challenge = result.Should().BeOfType<ChallengeResult>().Subject;
        challenge.AuthenticationSchemes.Should().Equal(authenticationScheme);
        challenge.Properties!.Parameters.TryGetValue(Parameters.Prompt, out var prompt)
            .Should().Be(forwardsPrompt);
        if (forwardsPrompt)
        {
            prompt.Should().Be(PromptValues.SelectAccount);
        }
        challenge.Properties.RedirectUri.Should().Be(
            $"/connect/authorize?client_id=relisten-mobile-ios&provider={provider}&state=test-state");
    }

    [Test]
    public async Task Development_account_selection_consumes_the_prompt_before_returning()
    {
        var request = new OpenIddictRequest
        {
            ClientId = "relisten-mobile-ios-dev",
            Prompt = PromptValues.SelectAccount
        };
        request.SetParameter("provider", AuthenticationConstants.GoogleProvider);
        var context = new DefaultHttpContext();
        context.Request.Path = "/connect/authorize";
        context.Request.QueryString = new QueryString(
            "?client_id=relisten-mobile-ios-dev&provider=google&prompt=select_account&state=test-state");
        context.Request.Headers.Cookie = $"{AuthenticationConstants.AuthSsoCookie}=opaque-session";
        context.Features.Set(new OpenIddictServerAspNetCoreFeature
        {
            Transaction = new OpenIddictServerTransaction { Request = request }
        });
        var controller = new AuthorizationController(
            null!,
            new AccountsRuntimeConfiguration(
                new AccountsOptions { EnableDevelopmentPersonas = true },
                new Uri("https://auth.relisten.localhost:5443"),
                []),
            null!,
            null!,
            null!,
            null!,
            null!,
            TimeProvider.System)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };

        var result = await controller.Authorize(CancellationToken.None);

        var redirect = result.Should().BeOfType<RedirectResult>().Subject;
        var query = QueryHelpers.ParseQuery(new Uri("https://localhost" + redirect.Url).Query);
        query["return_url"].Single().Should().Be(
            "/connect/authorize?client_id=relisten-mobile-ios-dev&provider=google&state=test-state");
    }

    private static AccountsRuntimeConfiguration Runtime(string provider) => new(
        new AccountsOptions
        {
            EnableExternalProviders = true,
            Google = new() { Enabled = provider == AuthenticationConstants.GoogleProvider },
            Apple = new() { Enabled = provider == AuthenticationConstants.AppleProvider }
        },
        new Uri("https://auth.relisten.net"),
        []);
}
