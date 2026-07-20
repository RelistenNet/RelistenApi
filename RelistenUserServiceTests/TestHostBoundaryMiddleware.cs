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
