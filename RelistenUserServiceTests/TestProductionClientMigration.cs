using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using OpenIddict.EntityFrameworkCore.Models;

namespace RelistenUserServiceTests;

[TestFixture]
[NonParallelizable]
public sealed class TestProductionClientMigration
{
    private readonly PostgresIntegrationDatabase _database = new();

    [OneTimeSetUp]
    public Task SetUp() => _database.StartAsync();

    [OneTimeTearDown]
    public Task TearDown() => _database.StopAsync();

    [Test]
    public async Task Installs_the_public_ios_client_with_custom_scheme_and_pkce()
    {
        await using var dbContext = _database.CreateContext();
        var application = await dbContext
            .Set<OpenIddictEntityFrameworkCoreApplication<Guid>>()
            .SingleAsync(item => item.ClientId == "relisten-mobile-ios");

        application.ClientType.Should().Be("public");
        application.ConsentType.Should().Be("implicit");
        JsonSerializer.Deserialize<string[]>(application.RedirectUris!)
            .Should().Equal("net.relisten.mobile:/oauth2redirect/ios");
        JsonSerializer.Deserialize<string[]>(application.Requirements!)
            .Should().ContainSingle().Which.Should().Be("ft:pkce");

        var permissions = JsonSerializer.Deserialize<string[]>(application.Permissions!);
        permissions.Should().Contain(
        [
            "ept:authorization",
            "ept:token",
            "gt:authorization_code",
            "gt:refresh_token",
            "rst:code",
            "scp:user.read",
            "scp:library.read",
            "scp:library.write",
            "scp:account.manage"
        ]);
    }
}
