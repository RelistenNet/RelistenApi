using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using OpenIddict.Abstractions;
using OpenIddict.EntityFrameworkCore.Models;
using RelistenUserService.Persistence;

namespace RelistenUserServiceTests;

[TestFixture]
[NonParallelizable]
public sealed class TestProductionClientMigration
{
    private readonly PostgresIntegrationDatabase _database = new();

    [OneTimeSetUp]
    public Task SetUp() => _database.StartAsync("20261031035630_StoreTypedAccountIds");

    [OneTimeTearDown]
    public Task TearDown() => _database.StopAsync();

    [Test]
    public async Task Upgrades_existing_ios_registration_to_the_exact_https_callback_with_pkce()
    {
        await using var dbContext = _database.CreateContext();
        var original = await dbContext
            .Set<OpenIddictEntityFrameworkCoreApplication<Guid>>()
            .AsNoTracking()
            .SingleAsync(item => item.ClientId == "relisten-mobile-ios");

        await dbContext.Database.MigrateAsync();
        var application = await dbContext
            .Set<OpenIddictEntityFrameworkCoreApplication<Guid>>()
            .SingleAsync(item => item.ClientId == "relisten-mobile-ios");

        application.Id.Should().Be(original.Id);
        application.Permissions.Should().Be(original.Permissions);
        application.ClientType.Should().Be("public");
        application.ConsentType.Should().Be("implicit");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(dbContext);
        services
            .AddOpenIddict()
            .AddCore(options =>
                options
                    .UseEntityFrameworkCore()
                    .UseDbContext<AccountsDbContext>()
                    .ReplaceDefaultEntities<Guid>()
            );
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        const string callback = "https://relisten.net/auth/mobile/ios/callback";
        (await manager.ValidateRedirectUriAsync(application, callback)).Should().BeTrue();
        (await manager.ValidateRedirectUriAsync(application, callback + "/other"))
            .Should()
            .BeFalse();
        (
            await manager.ValidateRedirectUriAsync(
                application,
                "net.relisten.mobile:/oauth2redirect/ios"
            )
        )
            .Should()
            .BeFalse();
        JsonSerializer
            .Deserialize<string[]>(application.Requirements!)
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be("ft:pkce");

        var permissions = JsonSerializer.Deserialize<string[]>(application.Permissions!);
        permissions
            .Should()
            .Contain([
                "ept:authorization",
                "ept:token",
                "gt:authorization_code",
                "gt:refresh_token",
                "rst:code",
                "scp:user.read",
                "scp:library.read",
                "scp:library.write",
                "scp:account.manage",
            ]);
    }
}
