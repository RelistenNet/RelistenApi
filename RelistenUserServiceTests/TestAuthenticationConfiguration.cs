using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using OpenIddict.Server;
using RelistenUserService.Authentication;
using RelistenUserService.Configuration;

namespace RelistenUserServiceTests;

[TestFixture]
public sealed class TestAuthenticationConfiguration
{
    private const string CertificatePassword = "test-password";
    private string _certificateDirectory = "";

    [SetUp]
    public void SetUp()
    {
        _certificateDirectory = Path.Combine(
            Path.GetTempPath(),
            $"relisten-auth-certs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_certificateDirectory);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_certificateDirectory))
        {
            Directory.Delete(_certificateDirectory, recursive: true);
        }
    }

    [Test]
    public void RegistersBothTokenEndpointSpellings()
    {
        using var provider = BuildProvider(CreateDevelopmentOptions(), Environments.Development);

        var options = provider.GetRequiredService<IOptions<OpenIddictServerOptions>>().Value;

        options.TokenEndpointUris.Select(uri => uri.OriginalString).Should().Equal(
            "/connect/token",
            "/connect/token/");
    }

    [Test]
    public void RegistersCurrentAndPreviousOpenIddictCertificates()
    {
        var previous = CreateCertificate("previous.pfx", DateTimeOffset.UtcNow.AddMonths(6));
        var current = CreateCertificate("current.pfx", DateTimeOffset.UtcNow.AddYears(1));
        var options = new AccountsOptions
        {
            Issuer = "https://auth.relisten.test",
            Audience = "https://accounts.relisten.test",
            AuthHost = "auth.relisten.test",
            AccountsHost = "accounts.relisten.test",
            TrustedProxyNetworks = ["127.0.0.1/32"],
            SigningCertificatePath = current,
            SigningCertificatePassword = CertificatePassword,
            PreviousSigningCertificatePath = previous,
            PreviousSigningCertificatePassword = CertificatePassword,
            EncryptionCertificatePath = current,
            EncryptionCertificatePassword = CertificatePassword,
            PreviousEncryptionCertificatePath = previous,
            PreviousEncryptionCertificatePassword = CertificatePassword,
            DataProtectionCertificatePath = current,
            DataProtectionCertificatePassword = CertificatePassword,
            PreviousDataProtectionCertificatePath = previous,
            PreviousDataProtectionCertificatePassword = CertificatePassword
        };
        using var provider = BuildProvider(options, Environments.Production);

        var server = provider.GetRequiredService<IOptions<OpenIddictServerOptions>>().Value;

        server.SigningCredentials.Should().HaveCount(2);
        server.EncryptionCredentials.Should().HaveCount(2);
    }

    private ServiceProvider BuildProvider(AccountsOptions options, string environmentName)
    {
        var environment = new TestHostEnvironment(environmentName);
        var runtime = AccountsRuntimeConfiguration.Create(options, environment);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Accounts"] =
                    "Host=localhost;Database=relisten;Username=relisten;Password=test",
                ["ConnectionStrings:AccountsLock"] =
                    "Host=localhost;Database=relisten;Username=relisten;Password=test"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRelistenAccounts(configuration, environment, runtime);
        return services.BuildServiceProvider();
    }

    private string CreateCertificate(string fileName, DateTimeOffset notAfter)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=Relisten test",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            critical: true));
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            notAfter);
        var path = Path.Combine(_certificateDirectory, fileName);
        File.WriteAllBytes(
            path,
            certificate.Export(X509ContentType.Pfx, CertificatePassword));
        return path;
    }

    private static AccountsOptions CreateDevelopmentOptions() => new()
    {
        Issuer = "http://localhost:5443",
        Audience = "https://accounts.relisten.test",
        AuthHost = "localhost",
        AccountsHost = "localhost",
        EnableDevelopmentPersonas = true,
        AllowInsecureHttp = true
    };

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "RelistenUserServiceTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
