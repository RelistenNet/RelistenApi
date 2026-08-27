using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Net.Http.Headers;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
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

    [Test]
    public async Task Relays_a_reviewed_shared_resource_from_the_accounts_backend()
    {
        var reachedApplication = false;
        var middleware = new WebOriginRelayMiddleware(
            context =>
            {
                reachedApplication = true;
                context.Features.Get<IWebOriginFeature>()!.Origin.Should()
                    .Be(AuthenticationConstants.LocalWebOrigin);
                return Task.CompletedTask;
            },
            Runtime());
        var context = Context("/v1/me", "accounts.relisten.net");
        context.Request.Headers[AuthenticationConstants.WebOriginHeader] =
            AuthenticationConstants.LocalWebOrigin;

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
    [TestCase("/api/user/v1/csrf", true)]
    [TestCase("/v1/me", true)]
    [TestCase("/v1/library/new-read-model", true)]
    [TestCase("/api/user/v1/me", false)]
    [TestCase("/v1/playback", false)]
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

    [TestCase("GET", "/auth/session/start", true)]
    [TestCase("POST", "/auth/session/new-mutation", true)]
    [TestCase("GET", "/api/user/v1/csrf", true)]
    [TestCase("GET", "/v1/me", true)]
    [TestCase("PATCH", "/v1/me", true)]
    [TestCase("GET", "/v1/library/new-read-model", true)]
    [TestCase("DELETE", "/v1/library/new-mutation", true)]
    [TestCase("GET", "/auth/session-evil", false)]
    [TestCase("GET", "/api/user/v1/me", false)]
    [TestCase("GET", "/v1/playback", false)]
    [TestCase("GET", "/v1/me/", false)]
    public void Relay_boundary_uses_reviewed_route_families(
        string method,
        string path,
        bool expected)
    {
        var context = Context(path, "accounts.relisten.net");
        context.Request.Method = method;

        BrowserRouteBoundary.CanRelay(context.Request.Path).Should().Be(expected);
    }

    [TestCase("v1/library/new-action", AuthenticationConstants.LibraryAccessPolicy)]
    [TestCase("auth/session/new-action", AuthenticationConstants.BrowserProfileReadPolicy)]
    [TestCase("api/user/v1/csrf", AuthenticationConstants.BrowserProfileReadPolicy)]
    [TestCase("v1/library-evil", null)]
    [TestCase("v1/playback", null)]
    public void Reviewed_controller_route_families_inherit_authorization(
        string route,
        string? expectedPolicy)
    {
        var conventions = new EndpointConventionProbe();
        conventions.RequireReviewedBrowserAuthorization();
        var endpoint = conventions.Build(route);

        endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Select(data => data.Policy)
            .Should().Equal(expectedPolicy is null ? [] : [expectedPolicy]);
    }

    [Test]
    public void Selects_exactly_one_account_credential_scheme()
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

        var both = Context("/v1/me", "accounts.relisten.net");
        both.Request.Headers.Authorization = "Bearer native-credential";
        both.Request.Headers.Cookie =
            $"{AuthenticationConstants.WebSessionCookie}=web-credential";
        AccountCredentialSelector.SelectScheme(both).Should().Be(
            AuthenticationConstants.RejectedAccountCredentialScheme);
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
        AllowLoopbackHttp: false,
        TrustedProxyNetworks: []);

    private sealed class EndpointConventionProbe : IEndpointConventionBuilder
    {
        private readonly List<Action<EndpointBuilder>> _conventions = [];

        public void Add(Action<EndpointBuilder> convention) =>
            _conventions.Add(convention);

        public Endpoint Build(string route)
        {
            var builder = new RouteEndpointBuilder(
                _ => Task.CompletedTask,
                RoutePatternFactory.Parse(route),
                order: 0);
            builder.Metadata.Add(new ControllerActionDescriptor
            {
                AttributeRouteInfo = new() { Template = route }
            });
            foreach (var convention in _conventions)
            {
                convention(builder);
            }

            return builder.Build();
        }
    }
}
