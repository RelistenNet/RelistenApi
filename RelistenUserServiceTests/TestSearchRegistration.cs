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
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?> { ["Search:Typesense:ApiKey"] = "writer-key" }
            )
            .Build();
        var environment = new HostingEnvironment { EnvironmentName = environmentName };
        var runtime = new AccountsRuntimeConfiguration(
            new AccountsOptions(),
            new Uri("https://auth.example.test/"),
            []
        );
        var services = new ServiceCollection();

        Action register = () => services.AddMergedSearch(runtime, configuration, environment);

        register.Should().Throw<InvalidOperationException>();
    }
}
