using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting.Internal;
using NUnit.Framework;
using RelistenUserService.Configuration;
using RelistenUserService.Search;

namespace RelistenUserServiceTests;

[TestFixture]
public sealed class TestSearchRegistration
{
    [TestCase("Staging")]
    [TestCase("Production")]
    public void SearchCannotStartWithOnlyTheWriterKey(string environmentName)
    {
        Action register = () => Register(environmentName, "ApiKey");

        register.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void DevelopmentCanStartWithoutAKey()
    {
        Action register = () => Register("Development");

        register.Should().NotThrow();
    }

    [Test]
    public void ProductionCanStartWithTheSearchKey()
    {
        Action register = () => Register("Production", "SearchKey");

        register.Should().NotThrow();
    }

    private static void Register(string environmentName, string? keyName = null)
    {
        var configuration = new ConfigurationManager();
        if (keyName is not null)
        {
            configuration[$"Search:Typesense:{keyName}"] = "test-key";
        }

        var environment = new HostingEnvironment { EnvironmentName = environmentName };
        var runtime = new AccountsRuntimeConfiguration(
            new AccountsOptions(),
            new Uri("https://auth.example.test/"),
            []
        );
        new ServiceCollection().AddMergedSearch(runtime, configuration, environment);
    }
}
