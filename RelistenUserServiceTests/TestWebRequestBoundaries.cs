using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using NUnit.Framework;
using OpenIddict.Client.AspNetCore;
using OpenIddict.Validation.AspNetCore;
using RelistenUserService.Authentication;
using RelistenUserService.Authentication.Authorization;
using RelistenUserService.Authentication.Browser;
using RelistenUserService.Authentication.Sessions;
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

    [TestCase("/auth/session/callback")]
    [TestCase("/v1/me")]
    public async Task Relays_an_exact_configured_web_origin_on_a_reviewed_route(
        string path)
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
        var context = Context(path, "accounts.relisten.net");
        context.Request.Headers[AuthenticationConstants.WebOriginHeader] =
            "https://web.relisten.localhost:5173";

        await middleware.InvokeAsync(context);

        reachedApplication.Should().BeTrue();
    }

    [TestCase("accounts.relisten.net")]
    [TestCase("ACCOUNTS.RELISTEN.NET")]
    public async Task Leaves_a_native_shared_resource_on_the_accounts_host_unchanged(
        string host)
    {
        var reachedApplication = false;
        var middleware = new WebOriginRelayMiddleware(
            context =>
            {
                reachedApplication = true;
                context.Request.Host.Should().Be(new HostString(host));
                context.Features.Get<IWebOriginFeature>().Should().BeNull();
                return Task.CompletedTask;
            },
            Runtime());
        var context = Context("/v1/me", host);

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
    public async Task Relays_every_method_on_a_shared_path_for_API_authorization()
    {
        var reachedApplication = false;
        var middleware = new WebOriginRelayMiddleware(
            _ =>
            {
                reachedApplication = true;
                return Task.CompletedTask;
            },
            Runtime());
        var context = Context("/v1/me", "accounts.relisten.net");
        context.Request.Method = HttpMethods.Patch;
        context.Request.Headers[AuthenticationConstants.WebOriginHeader] =
            AuthenticationConstants.LocalWebOrigin;

        await middleware.InvokeAsync(context);

        reachedApplication.Should().BeTrue();
    }

    [Test]
    public async Task Rejects_a_valid_relay_from_an_unexpected_backend_host()
    {
        var middleware = new WebOriginRelayMiddleware(_ => Task.CompletedTask, Runtime());
        var context = Context("/v1/me", "evil.example");
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

    [TestCase(true, AuthenticationConstants.GoogleProvider)]
    [TestCase(false, AuthenticationConstants.AppleProvider)]
    public void Session_start_selects_the_local_registration_and_enabled_provider(
        bool googleEnabled,
        string expectedProvider)
    {
        var context = Context(
            "/auth/session/start",
            "web.relisten.localhost:5173");
        context.Features.Set<IWebOriginFeature>(new WebOriginFeature(
            AuthenticationConstants.LocalWebOrigin));
        var runtime = new AccountsRuntimeConfiguration(
            new AccountsOptions
            {
                Google = new() { Enabled = googleEnabled },
                Apple = new() { Enabled = !googleEnabled }
            },
            new Uri("https://auth.relisten.test"),
            []);
        var controller = new WebSessionController(runtime, null!, null!, null!, null!)
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
            .Should().Be(expectedProvider);
    }

    [Test]
    public async Task Auth_cookie_clear_is_idempotent_after_another_tab_clears_the_cookie()
    {
        var context = Context("/auth/sso/clear", "auth.relisten.net");
        var controller = new AuthSsoCookieController(
            Runtime(),
            null!,
            new SessionCookieManager())
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };

        var result = await controller.Clear(
            AuthenticationConstants.LocalWebOrigin,
            "/library",
            CancellationToken.None);

        result.Should().BeOfType<RedirectResult>()
            .Which.Url.Should().Be(
                AuthenticationConstants.LocalWebOrigin + "/library");
        context.Response.Headers.SetCookie.Should().BeEmpty();
    }

    [TestCase("/auth/session/callback", true)]
    [TestCase("/auth/sso/clear", true)]
    [TestCase("/api/user/v1/csrf", true)]
    [TestCase("/v1/me", true)]
    [TestCase("/v1/library/new-read-model", true)]
    [TestCase("/v1/new-account-route", true)]
    [TestCase("/api/user/v1/me", false)]
    [TestCase("/v1evil", false)]
    [TestCase("/auth/session-evil", false)]
    [TestCase("/api/user/v10/me", false)]
    public async Task No_store_applies_to_account_and_browser_session_families(
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

    [TestCase("/auth/session/start", true)]
    [TestCase("/api/user/v1/csrf", true)]
    [TestCase("/v1/me", true)]
    [TestCase("/v1/library/new-read-model", true)]
    [TestCase("/auth/session-evil", false)]
    [TestCase("/api/user/v1/me", false)]
    [TestCase("/v1/playback", false)]
    [TestCase("/v1/me/", false)]
    public void Relay_boundary_uses_reviewed_route_families(
        string path,
        bool expected)
    {
        var context = Context(path, "accounts.relisten.net");

        BrowserRouteBoundary.CanRelay(context.Request.Path).Should().Be(expected);
    }

    [Test]
    public async Task Library_actions_inherit_authorization_through_real_MVC_routing()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(ReviewedBrowserConventionProbeController)
                .Assembly.FullName
        });
        builder.Services.AddControllers()
            .AddApplicationPart(typeof(ReviewedBrowserConventionProbeController).Assembly);
        await using var app = builder.Build();
        app.MapControllers().RequireReviewedBrowserAuthorization();

        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToDictionary(endpoint => endpoint.RoutePattern.RawText!);

        endpoints["v1/library/convention-probe"].Metadata
            .GetOrderedMetadata<IAuthorizeData>()
            .Select(data => data.Policy)
            .Should().ContainSingle()
            .Which.Should().Be(AuthenticationConstants.LibraryAccessPolicy);
        endpoints["v1/unreviewed-convention-probe"].Metadata
            .GetOrderedMetadata<IAuthorizeData>()
            .Should().BeEmpty();
    }

    [Test]
    public void Selects_the_native_or_web_credential_scheme()
    {
        var bearer = Context("/v1/me", "accounts.relisten.net");
        bearer.Request.Headers.Authorization = "Bearer native-credential";
        AccountCredentialSelector.SelectScheme(bearer).Should().Be(
            OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);

        var web = Context("/v1/me", "accounts.relisten.net");
        web.Request.Headers.Cookie =
            $"{AuthenticationConstants.WebSessionCookie}=web-credential";
        AccountCredentialSelector.SelectScheme(web).Should().Be(
            AuthenticationConstants.WebSessionScheme);
    }

    [Test]
    public async Task Rejects_simultaneous_credentials_before_the_application_runs()
    {
        var reachedApplication = false;
        var middleware = new AccountCredentialAmbiguityMiddleware(_ =>
        {
            reachedApplication = true;
            return Task.CompletedTask;
        });
        var context = Context("/v1/me", "accounts.relisten.net");
        context.Request.Headers.Authorization = "Bearer native-credential";
        context.Request.Headers.Cookie =
            $"{AuthenticationConstants.WebSessionCookie}=web-credential";

        await middleware.InvokeAsync(context);

        reachedApplication.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    private static DefaultHttpContext Context(string path, string host)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString(host);
        context.Request.Path = path;
        context.Request.Method = HttpMethods.Get;
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
        TrustedProxyNetworks: []);
}

[ApiController]
public sealed class ReviewedBrowserConventionProbeController : ControllerBase
{
    [HttpGet("v1/library/convention-probe")]
    public IActionResult Library() => Ok();

    [HttpGet("v1/unreviewed-convention-probe")]
    public IActionResult Unreviewed() => Ok();
}
