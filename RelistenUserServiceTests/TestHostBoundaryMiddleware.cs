using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using RelistenUserService.Authentication;
using RelistenUserService.Authentication.Browser;
using RelistenUserService.Configuration;

namespace RelistenUserServiceTests;

[TestFixture]
public sealed class TestHostBoundaryMiddleware
{
    [Test]
    public async Task Production_host_filter_allows_the_canonical_browser_host()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(TestContext.CurrentContext.TestDirectory)
            .AddJsonFile("appsettings.json")
            .Build();
        var allowedHosts = configuration["AllowedHosts"]!
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        using var services = new ServiceCollection()
            .AddLogging()
            .AddHostFiltering(options => options.AllowedHosts = allowedHosts)
            .BuildServiceProvider();
        var reachedApplication = false;
        var app = new ApplicationBuilder(services);
        app.UseHostFiltering();
        app.Run(_ =>
        {
            reachedApplication = true;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Host = new HostString("relisten.net");

        await app.Build()(context);

        reachedApplication.Should().BeTrue();
    }

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

    [TestCase("/auth/session/start", "accounts.relisten.net", true)]
    [TestCase("/auth/session/start", "relisten.net", true)]
    [TestCase("/api/user/v1/csrf", "accounts.relisten.net", true)]
    [TestCase("/v1/me", "accounts.relisten.net", true)]
    [TestCase("/v1/me", "relisten.net", true)]
    [TestCase("/v1/me", "web.relisten.localhost:5173", true)]
    [TestCase("/v1/library/new-action", "web.relisten.localhost:5173", true)]
    [TestCase("/v1/library-evil", "web.relisten.localhost:5173", false)]
    [TestCase("/v1/logout", "web.relisten.localhost:5173", false)]
    [TestCase("/v1/not-reviewed", "web.relisten.localhost:5173", false)]
    [TestCase("/auth/session/start", "relisten.net:8443", false)]
    [TestCase("/v1/me", "accounts.relisten.net:8443", false)]
    [TestCase("/health/live", "accounts.relisten.net", true)]
    [TestCase("/health/live", "evil.example", false)]
    [TestCase("/future-route", "auth.relisten.net", true)]
    [TestCase("/future-route", "evil.example", false)]
    public async Task Browser_and_native_routes_use_exact_host_and_port_boundaries(
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
        TrustedProxyNetworks: []);
}
