using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using OpenIddict.Client;
using OpenIddict.Client.AspNetCore;
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
    public void Registers_two_exact_confidential_web_callbacks_with_query_and_s256()
    {
        using var provider = BuildProvider(CreateDevelopmentOptions(), Environments.Development);

        var options = provider.GetRequiredService<IOptions<OpenIddictClientOptions>>().Value;
        var registrations = options.Registrations
            .Where(registration => registration.ClientId == AuthenticationConstants.WebClientId)
            .OrderBy(registration => registration.RegistrationId)
            .ToArray();

        registrations.Should().HaveCount(2);
        registrations.Select(registration => new
        {
            registration.RegistrationId,
            RedirectUri = registration.RedirectUri!.AbsoluteUri
        }).Should().BeEquivalentTo(
        [
            new
            {
                RegistrationId = AuthenticationConstants.CanonicalWebRegistration,
                RedirectUri = AuthenticationConstants.CanonicalWebCallback
            },
            new
            {
                RegistrationId = AuthenticationConstants.LocalWebRegistration,
                RedirectUri = AuthenticationConstants.LocalWebCallback
            }
        ]);
        registrations.Should().OnlyContain(registration =>
            HasExactWebProtocolShape(registration));
    }

    [Test]
    public void OpenIddict_correlation_cookie_supports_cross_site_Apple_form_posts()
    {
        using var provider = BuildProvider(CreateDevelopmentOptions(), Environments.Development);

        var options = provider
            .GetRequiredService<IOptions<OpenIddictClientAspNetCoreOptions>>()
            .Value;

        options.CookieBuilder.HttpOnly.Should().BeTrue();
        options.CookieBuilder.SecurePolicy.Should().Be(CookieSecurePolicy.Always);
        options.CookieBuilder.SameSite.Should().Be(SameSiteMode.None);
    }

    [Test]
    public void External_providers_fail_closed_when_a_secret_is_missing()
    {
        var environment = new TestHostEnvironment(Environments.Production);
        var options = new AccountsOptions
        {
            Issuer = "https://auth.relisten.net",
            AuthHost = "auth.relisten.net",
            AccountsHost = "accounts.relisten.net",
            TrustedProxyNetworks = ["127.0.0.1/32"],
            EnableExternalProviders = true,
            WebClientSecret = "test-web-client-secret",
            Google = new()
            {
                Enabled = true,
                ClientId = "google-client"
            },
            Apple = new()
            {
                Enabled = true,
                ClientId = "apple-client",
                TeamId = "apple-team",
                KeyId = "apple-key",
                PrivateKeyPath = "/run/secrets/apple.p8"
            }
        };

        var action = () => AccountsRuntimeConfiguration.Create(options, environment);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*Accounts:Google:ClientSecret*");
    }

    [Test]
    public void Local_Google_registers_without_loading_Apple_credentials()
    {
        using var provider = BuildProvider(
            CreateLocalGoogleOptions(),
            Environments.Development);

        var registrations = provider
            .GetRequiredService<IOptions<OpenIddictClientOptions>>()
            .Value
            .Registrations;

        registrations.Should().ContainSingle(
            registration => registration.RegistrationId
                == AuthenticationConstants.GoogleProvider);
        registrations.Should().NotContain(
            registration => registration.RegistrationId
                == AuthenticationConstants.AppleProvider);
    }

    [Test]
    public void Production_external_sign_in_requires_Apple()
    {
        var environment = new TestHostEnvironment(Environments.Production);
        var options = new AccountsOptions
        {
            Issuer = "https://auth.relisten.net",
            AuthHost = "auth.relisten.net",
            AccountsHost = "accounts.relisten.net",
            TrustedProxyNetworks = ["127.0.0.1/32"],
            EnableExternalProviders = true,
            WebClientSecret = "test-web-client-secret",
            Google = new()
            {
                Enabled = true,
                ClientId = "google-client",
                ClientSecret = "google-secret"
            },
            Apple = new()
            {
                Enabled = false
            }
        };

        var action = () => AccountsRuntimeConfiguration.Create(options, environment);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*requires both Google and Apple*");
    }

    [Test]
    public void Rejects_a_web_origin_that_the_session_schema_cannot_store()
    {
        var environment = new TestHostEnvironment(Environments.Development);
        var options = new AccountsOptions
        {
            Issuer = "https://auth.relisten.localhost:5443",
            Audience = "https://accounts.relisten.test",
            AuthHost = "auth.relisten.localhost:5443",
            AccountsHost = "accounts.relisten.localhost:5443",
            EnableDevelopmentPersonas = true,
            DevelopmentCertificateAuthorityPath =
                CreateCertificateAuthority("unsupported-origin-ca.pem"),
            WebClientSecret = "test-web-client-secret",
            WebOrigins = ["https://preview.relisten.localhost:5173"]
        };

        var action = () => AccountsRuntimeConfiguration.Create(options, environment);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*unsupported exact HTTPS origin*");
    }

    [Test]
    public void ReusesDevelopmentCertificatesAcrossServiceProviders()
    {
        using var firstProvider = BuildProvider(CreateDevelopmentOptions(), Environments.Development);
        var first = firstProvider.GetRequiredService<IOptions<OpenIddictServerOptions>>().Value;
        var firstSigningThumbprint = first.SigningCredentials.Single().Key.KeyId;
        var firstEncryptionThumbprint = first.EncryptionCredentials.Single().Key.KeyId;

        using var secondProvider = BuildProvider(CreateDevelopmentOptions(), Environments.Development);
        var second = secondProvider.GetRequiredService<IOptions<OpenIddictServerOptions>>().Value;

        second.SigningCredentials.Single().Key.KeyId.Should().Be(firstSigningThumbprint);
        second.EncryptionCredentials.Single().Key.KeyId.Should().Be(firstEncryptionThumbprint);
    }

    [Test]
    public void RegistersCurrentAndPreviousOpenIddictCertificates()
    {
        var previous = CreateCertificate("previous.pfx", DateTimeOffset.UtcNow.AddMonths(6));
        var current = CreateCertificate("current.pfx", DateTimeOffset.UtcNow.AddYears(1));
        var options = new AccountsOptions
        {
            Issuer = "https://auth.relisten.net",
            Audience = "https://accounts.relisten.test",
            AuthHost = "auth.relisten.net",
            AccountsHost = "accounts.relisten.net",
            TrustedProxyNetworks = ["127.0.0.1/32"],
            WebClientSecret = "test-web-client-secret",
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
        var environment = new TestHostEnvironment(environmentName)
        {
            ContentRootPath = _certificateDirectory
        };
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

    private static bool HasExactWebProtocolShape(OpenIddictClientRegistration registration) =>
        registration.ClientType
            == OpenIddict.Abstractions.OpenIddictConstants.ClientTypes.Confidential
        && registration.CodeChallengeMethods.SetEquals(
            new[] { OpenIddict.Abstractions.OpenIddictConstants.CodeChallengeMethods.Sha256 })
        && registration.ResponseModes.SetEquals(
            new[] { OpenIddict.Abstractions.OpenIddictConstants.ResponseModes.Query })
        && registration.Scopes.SetEquals(
            new[]
            {
                OpenIddict.Abstractions.OpenIddictConstants.Scopes.OpenId,
                OpenIddict.Abstractions.OpenIddictConstants.Scopes.Profile
            });

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

    private string CreateCertificateAuthority(string fileName)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=Relisten test development CA",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(
            certificateAuthority: true,
            hasPathLengthConstraint: false,
            pathLengthConstraint: 0,
            critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
            critical: true));
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(1));
        var path = Path.Combine(_certificateDirectory, fileName);
        File.WriteAllText(path, certificate.ExportCertificatePem());
        return path;
    }

    private AccountsOptions CreateDevelopmentOptions() => new()
    {
        Issuer = "https://auth.relisten.localhost:5443",
        Audience = "https://accounts.relisten.test",
        AuthHost = "auth.relisten.localhost:5443",
        AccountsHost = "accounts.relisten.localhost:5443",
        WebOrigins = ["https://web.relisten.localhost:5173"],
        EnableDevelopmentPersonas = true,
        DevelopmentCertificateAuthorityPath =
            CreateCertificateAuthority($"development-ca-{Guid.NewGuid():N}.pem"),
        WebClientSecret = "test-web-client-secret"
    };

    private AccountsOptions CreateLocalGoogleOptions() => new()
    {
        Issuer = "https://localhost:5443",
        Audience = "https://accounts.relisten.test",
        AuthHost = "localhost:5443",
        AccountsHost = "accounts.relisten.localhost:5443",
        WebOrigins = ["https://web.relisten.localhost:5173"],
        EnableExternalProviders = true,
        DevelopmentCertificateAuthorityPath =
            CreateCertificateAuthority($"google-development-ca-{Guid.NewGuid():N}.pem"),
        WebClientSecret = "test-web-client-secret",
        Google = new()
        {
            Enabled = true,
            ClientId = "local-google-client",
            ClientSecret = "local-google-secret"
        },
        Apple = new()
        {
            Enabled = false
        }
    };

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "RelistenUserServiceTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
