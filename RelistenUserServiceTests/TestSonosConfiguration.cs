using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using RelistenUserService.Authentication.Browser;
using RelistenUserService.Configuration;
using RelistenUserService.Sonos;

namespace RelistenUserServiceTests;

[TestFixture]
public sealed class TestSonosConfiguration
{
    private static readonly AccountsRuntimeConfiguration Production = new(
        new AccountsOptions(),
        new Uri("https://auth.relisten.net/"),
        []
    );

    [Test]
    public async Task Credentials_expose_the_callback_on_the_accounts_host()
    {
        var config = SonosConfiguration.Read(Configured(), Production)!;
        config.RedirectUri.Should().Be("https://accounts.relisten.net/sonos/oauth/callback");
        var middleware = new HostBoundaryMiddleware(
            context =>
            {
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return Task.CompletedTask;
            },
            Production,
            new SonosIntegration(config)
        );
        var request = new DefaultHttpContext();
        request.Request.Path = config.RedirectUri.AbsolutePath;
        request.Request.Host = new HostString("accounts.relisten.net");
        await middleware.InvokeAsync(request);
        request.Response.StatusCode.Should().Be(StatusCodes.Status204NoContent);

        request.Request.Host = new HostString("auth.relisten.net");
        await middleware.InvokeAsync(request);
        request.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Test]
    public void No_sonos_configuration_allows_accounts_only_startup()
    {
        SonosConfiguration.Read(new ConfigurationBuilder().Build(), Production).Should().BeNull();
    }

    [Test]
    public void Partial_credentials_or_an_off_host_callback_fail_startup()
    {
        var partial = Configured();
        partial["Sonos:ClientSecret"] = null;
        var offHost = Configured();
        offHost["Sonos:PublicBaseUrl"] = "https://tunnel.example.test/";

        var readPartial = () => SonosConfiguration.Read(partial, Production);
        var readOffHost = () => SonosConfiguration.Read(offHost, Production);
        readPartial.Should().Throw<InvalidOperationException>();
        readOffHost.Should().Throw<InvalidOperationException>();
    }

    private static IConfiguration Configured() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Sonos:ClientId"] = "test-client",
                    ["Sonos:ClientSecret"] = "test-secret",
                    ["Sonos:ServiceId"] = "270",
                }
            )
            .Build();
}
