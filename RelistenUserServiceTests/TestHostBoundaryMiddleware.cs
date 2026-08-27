using FluentAssertions;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;
using RelistenUserService.Authentication;
using RelistenUserService.Configuration;

namespace RelistenUserServiceTests;

[TestFixture]
public sealed class TestHostBoundaryMiddleware
{
    [TestCase("/signin-google")]
    [TestCase("/signin-apple")]
    public async Task Provider_callbacks_are_rejected_on_the_accounts_host(string path)
    {
        var reachedApplication = false;
        var middleware = new HostBoundaryMiddleware(
            _ =>
            {
                reachedApplication = true;
                return Task.CompletedTask;
            },
            Runtime());
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString("accounts.relisten.net");
        context.Request.Path = path;

        await middleware.InvokeAsync(context);

        reachedApplication.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [TestCase("GET", "/auth/session/start", "accounts.relisten.net", true)]
    [TestCase("GET", "/auth/session/start", "relisten.net", true)]
    [TestCase("GET", "/api/user/v1/csrf", "accounts.relisten.net", true)]
    [TestCase("GET", "/v1/me", "accounts.relisten.net", true)]
    [TestCase("GET", "/v1/me", "relisten.net", true)]
    [TestCase("GET", "/v1/me", "web.relisten.localhost:5173", true)]
    [TestCase("PATCH", "/v1/me", "accounts.relisten.net", true)]
    [TestCase("PATCH", "/v1/me", "web.relisten.localhost:5173", true)]
    [TestCase("GET", "/v1/library/new-action", "web.relisten.localhost:5173", true)]
    [TestCase("GET", "/v1/library-evil", "web.relisten.localhost:5173", false)]
    [TestCase("POST", "/v1/logout", "web.relisten.localhost:5173", false)]
    [TestCase("GET", "/v1/not-reviewed", "web.relisten.localhost:5173", false)]
    [TestCase("GET", "/auth/session/start", "relisten.net:8443", false)]
    [TestCase("GET", "/v1/me", "accounts.relisten.net:8443", false)]
    [TestCase("GET", "/health/live", "accounts.relisten.net", true)]
    [TestCase("GET", "/health/live", "evil.example", false)]
    [TestCase("GET", "/future-route", "auth.relisten.net", true)]
    [TestCase("GET", "/future-route", "evil.example", false)]
    public async Task Browser_and_native_routes_use_exact_host_and_port_boundaries(
        string method,
        string path,
        string host,
        bool expectedToReachApplication)
    {
        var reachedApplication = false;
        var middleware = new HostBoundaryMiddleware(
            _ =>
            {
                reachedApplication = true;
                return Task.CompletedTask;
            },
            Runtime());
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Host = new HostString(host);
        context.Request.Path = path;

        await middleware.InvokeAsync(context);

        reachedApplication.Should().Be(expectedToReachApplication);
        if (!expectedToReachApplication)
        {
            context.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        }
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
