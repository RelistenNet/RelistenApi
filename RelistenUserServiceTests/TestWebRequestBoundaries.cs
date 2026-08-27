using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using NUnit.Framework;
using OpenIddict.Client.AspNetCore;
using RelistenUserService.Authentication;
using RelistenUserService.Configuration;
using RelistenUserService.Identity.Entities;
using RelistenUserService.Http;

namespace RelistenUserServiceTests;

[TestFixture]
public sealed class TestWebRequestBoundaries
{
    [TestCase(null, "/")]
    [TestCase("/library", "/library")]
    [TestCase("/library?view=favorites", "/library?view=favorites")]
    public void Accepts_relative_application_return_paths(string? value, string expected)
    {
        ApplicationReturnPath.TryValidate(value, out var path).Should().BeTrue();
        path.Should().Be(expected);
    }

    [TestCase("https://evil.example/")]
    [TestCase("//evil.example/")]
    [TestCase("/\\evil.example/")]
    [TestCase("/library#token")]
    [TestCase("/library\\other")]
    public void Rejects_return_paths_that_can_leave_or_ambiguate_the_application(string value)
    {
        ApplicationReturnPath.TryValidate(value, out _).Should().BeFalse();
    }

    [Test]
    public async Task Relays_only_an_exact_configured_web_origin_from_an_expected_backend()
    {
        var reachedApplication = false;
        var middleware = new WebOriginRelayMiddleware(
            context =>
            {
                reachedApplication = true;
                context.Request.Scheme.Should().Be("https");
                context.Request.Host.Should().Be(new HostString("web.relisten.localhost", 5173));
                context.Features.Get<IWebOriginFeature>()!.Origin.Should()
                    .Be("https://web.relisten.localhost:5173");
                context.Request.Headers.ContainsKey(AuthenticationConstants.WebOriginHeader)
                    .Should().BeFalse();
                return Task.CompletedTask;
            },
            Runtime());
        var context = Context(
            "/auth/session/callback",
            "accounts.relisten.net");
        context.Request.Headers[AuthenticationConstants.WebOriginHeader] =
            "https://web.relisten.localhost:5173";

        await middleware.InvokeAsync(context);

        reachedApplication.Should().BeTrue();
    }

    [TestCase("/auth/session-evil", "https://web.relisten.localhost:5173")]
    [TestCase("/auth/session/start", "https://web.relisten.localhost:5174")]
    [TestCase("/auth/session/start", "http://web.relisten.localhost:5173")]
    public async Task Rejects_a_relay_outside_the_exact_prefix_or_origin(
        string path,
        string origin)
    {
        var reachedApplication = false;
        var middleware = new WebOriginRelayMiddleware(
            _ =>
            {
                reachedApplication = true;
                return Task.CompletedTask;
            },
            Runtime());
        var context = Context(path, "accounts.relisten.net");
        context.Request.Headers[AuthenticationConstants.WebOriginHeader] = origin;

        await middleware.InvokeAsync(context);

        reachedApplication.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task Rejects_a_valid_relay_from_an_unexpected_backend_host()
    {
        var middleware = new WebOriginRelayMiddleware(_ => Task.CompletedTask, Runtime());
        var context = Context("/api/user/v1/me", "evil.example");
        context.Request.Headers[AuthenticationConstants.WebOriginHeader] =
            "https://web.relisten.localhost:5173";

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Test]
    public void Session_cookies_have_the_exact_host_only_security_attributes()
    {
        var response = new DefaultHttpContext().Response;
        var expires = DateTimeOffset.UtcNow.AddDays(30);
        var session = new IssuedIdentitySession(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            IdentitySessionPurposes.Web,
            DateTimeOffset.UtcNow,
            Guid.CreateVersion7(),
            "https://web.relisten.localhost:5173",
            IdentitySessionCapabilities.AllWeb,
            expires,
            "opaque-test-value");

        new SessionCookieManager().SetWeb(response, session);
        var parsed = SetCookieHeaderValue.Parse(response.Headers.SetCookie.Single());
        var metadata = new
        {
            parsed.Name,
            parsed.Domain,
            parsed.Path,
            parsed.HttpOnly,
            parsed.Secure,
            parsed.SameSite
        };

        metadata.Name.Value.Should().Be(AuthenticationConstants.WebSessionCookie);
        metadata.Domain.HasValue.Should().BeFalse();
        metadata.Path.Value.Should().Be("/");
        metadata.HttpOnly.Should().BeTrue();
        metadata.Secure.Should().BeTrue();
        metadata.SameSite.ToString().Should().Be("Lax");
    }

    [Test]
    public void Session_start_selects_the_local_registration_and_google_provider()
    {
        var context = Context(
            "/auth/session/start",
            "web.relisten.localhost:5173");
        context.Features.Set<IWebOriginFeature>(new WebOriginFeature(
            AuthenticationConstants.LocalWebOrigin));
        var controller = new WebSessionController(null!, null!, null!, null!)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };

        var result = controller.Start("/library");

        var challenge = result.Should().BeOfType<ChallengeResult>().Subject;
        challenge.AuthenticationSchemes.Should().Equal(
            OpenIddictClientAspNetCoreDefaults.AuthenticationScheme);
        challenge.Properties!.RedirectUri.Should().Be("/library");
        challenge.Properties.Items[
            OpenIddictClientAspNetCoreConstants.Properties.RegistrationId]
            .Should().Be(AuthenticationConstants.LocalWebRegistration);
        challenge.Properties.Parameters["provider"]
            .Should().Be(AuthenticationConstants.GoogleProvider);
    }

    [TestCase("/auth/session/callback", true)]
    [TestCase("/api/user/v1/me", true)]
    [TestCase("/auth/session-evil", false)]
    [TestCase("/api/user/v10/me", false)]
    public async Task No_store_applies_only_to_the_reviewed_prefixes(
        string path,
        bool expected)
    {
        var middleware = new PrivateNoStoreMiddleware(async context =>
        {
            context.Response.Headers.CacheControl = "public, max-age=60";
            await context.Response.StartAsync();
        });
        var context = Context(path, "relisten.net");

        await middleware.InvokeAsync(context);

        context.Response.Headers.CacheControl.ToString().Should().Be(
            expected ? "private, no-store" : "public, max-age=60");
    }

    private static DefaultHttpContext Context(string path, string host)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString(host);
        context.Request.Path = path;
        return context;
    }

    private static AccountsRuntimeConfiguration Runtime() => new(
        new AccountsOptions
        {
            Issuer = "https://auth.relisten.net",
            AuthHost = "auth.relisten.net",
            AccountsHost = "accounts.relisten.net"
        },
        new Uri("https://auth.relisten.net"),
        AllowLoopbackHttp: false,
        TrustedProxyNetworks: []);
}
