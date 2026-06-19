using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Relisten.UserApi.Auth;

namespace RelistenUserApiTests;

[TestFixture]
public class UserLibraryEndpointTests
{
    [Test]
    public async Task Health_ShouldBePublic()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("OK");
    }

    [Test]
    public async Task UsersMe_WithoutAuth_ShouldReturn401AndNoStore()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v3/library/users/me");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    [Test]
    public async Task UsersMe_WithTestAuth_ShouldReturnSnakeCaseUser()
    {
        await using var factory = NewTestAuthFactory();
        using var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v3/library/users/me");
        request.Headers.Add(TestUserAuthenticationHandler.UserUuidHeader, "10000000-0000-0000-0000-000000000001");
        request.Headers.Add(TestUserAuthenticationHandler.DisplayNameHeader, "A Relisten User");
        request.Headers.Add(TestUserAuthenticationHandler.UsernameHeader, "a-relisten-user");
        request.Headers.Add(TestUserAuthenticationHandler.ScopeIdHeader, "user:10000000-0000-0000-0000-000000000001");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        body.Should().Contain("\"user_uuid\":\"10000000-0000-0000-0000-000000000001\"");
        body.Should().Contain("\"display_name\":\"A Relisten User\"");
        body.Should().Contain("\"scope_id\":\"user:10000000-0000-0000-0000-000000000001\"");
        body.Should().NotContain("UserUuid");
        body.Should().NotContain("DisplayName");
        body.Should().NotContain("ScopeId");
    }

    private static WebApplicationFactory<Program> NewTestAuthFactory()
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    services
                        .AddAuthentication(TestAuthScheme)
                        .AddScheme<AuthenticationSchemeOptions, TestUserAuthenticationHandler>(
                            TestAuthScheme,
                            _ => { });

                    services.PostConfigure<AuthenticationOptions>(options =>
                    {
                        options.DefaultAuthenticateScheme = TestAuthScheme;
                        options.DefaultChallengeScheme = TestAuthScheme;
                    });
                });
            });
    }

    private const string TestAuthScheme = "RelistenUserTest";
}
