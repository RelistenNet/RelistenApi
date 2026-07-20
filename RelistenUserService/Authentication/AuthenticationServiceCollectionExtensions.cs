using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Validation.AspNetCore;
using RelistenUserService.Configuration;
using RelistenUserService.Identity;
using RelistenUserService.Identity.Usernames;
using RelistenUserService.Library;
using RelistenUserService.Persistence;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace RelistenUserService.Authentication;

public static class AuthenticationServiceCollectionExtensions
{
    public static IServiceCollection AddRelistenAccounts(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment,
        AccountsRuntimeConfiguration runtime)
    {
        services.AddSingleton(runtime);
        services.AddSingleton(runtime.Options);
        services.AddSingleton(TimeProvider.System);
        // Validate the direct-primary exception during startup, not on the first refresh.
        _ = DatabaseConnectionString.ResolveRefreshTokenLocks(configuration, environment);
        services.AddSingleton<RefreshTokenLockProvider>();
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedHost
                | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;
            options.AllowedHosts.Add(runtime.Options.AuthHost);
            options.AllowedHosts.Add(runtime.Options.AccountsHost);
            foreach (var network in runtime.TrustedProxyNetworks)
            {
                options.KnownIPNetworks.Add(network);
            }
        });

        services.AddDbContext<AccountsDbContext>(options =>
            options.UseNpgsql(
                    DatabaseConnectionString.Resolve(configuration),
                    postgres => postgres.MigrationsHistoryTable(
                        "__EFMigrationsHistory",
                        "identity"))
                .UseOpenIddict<Guid>());

        var dataProtection = services.AddDataProtection()
            .SetApplicationName("Relisten.UserService")
            .PersistKeysToDbContext<AccountsDbContext>();
        if (!environment.IsDevelopment())
        {
            var certificates = LoadCertificateSet(
                runtime.Options.DataProtectionCertificatePath,
                runtime.Options.DataProtectionCertificatePassword,
                runtime.Options.PreviousDataProtectionCertificatePath,
                runtime.Options.PreviousDataProtectionCertificatePassword,
                "Data Protection wrapping");
            dataProtection
                .ProtectKeysWithCertificate(certificates[0])
                .UnprotectKeysWithAnyCertificate(certificates);
        }

        var openIddict = services.AddOpenIddict();
        openIddict.AddCore(options => options.UseEntityFrameworkCore()
                .UseDbContext<AccountsDbContext>()
                .ReplaceDefaultEntities<Guid>())
            .AddServer(options => ConfigureServer(options, environment, runtime))
            .AddValidation(options =>
            {
                options.UseLocalServer();
                options.UseAspNetCore();
            });

        if (runtime.Options.EnableExternalProviders)
        {
            openIddict.AddClient(options =>
                ConfigureExternalProviders(options, environment, runtime));
        }

        services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme =
                OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme =
                OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme;
        });

        if (runtime.Options.EnableDevelopmentPersonas)
        {
            services.AddAuthentication().AddCookie(
                AuthenticationConstants.DevelopmentIdentityScheme,
                options => ConfigureDevelopmentCookie(options));
            services.AddAntiforgery();
            services.AddSingleton<DevelopmentDatabaseInitializer>();
        }
        else if (runtime.Options.EnableExternalProviders)
        {
            services.AddAuthentication().AddCookie(
                AuthenticationConstants.ExternalIdentityScheme,
                ConfigureExternalIdentityCookie);
        }

        services.AddAuthorization(options =>
        {
            AddPolicy(options, AuthenticationConstants.UserReadPolicy, RelistenScopes.UserRead);
            AddPolicy(
                options,
                AuthenticationConstants.LibraryReadPolicy,
                RelistenScopes.LibraryRead);
            AddPolicy(
                options,
                AuthenticationConstants.LibraryWritePolicy,
                RelistenScopes.LibraryWrite);
            AddPolicy(
                options,
                AuthenticationConstants.AccountManagePolicy,
                RelistenScopes.AccountManage);
        });

        services.AddScoped<CurrentAccountContext>();
        services.AddScoped<IAuthorizationHandler, NativeSessionAuthorizationHandler>();
        services.AddSingleton<IAuthorizationHandler, ScopeAuthorizationHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler,
            NativeSessionAuthorizationResultHandler>();
        services.AddScoped<AdvisoryLockService>();
        services.AddScoped<UsernamePolicy>();
        services.AddScoped<UsernameReservationService>();
        services.AddScoped<UsernameCommandService>();
        services.AddScoped<ExternalIdentityCompletionService>();
        services.AddScoped<NativePrincipalFactory>();
        services.AddScoped<CatalogAvailabilityValidator>();
        services.AddScoped<FavoriteMutationService>();
        services.AddScoped<LibraryStateStore>();
        services.AddScoped<LibraryReadService>();
        services.AddSingleton<LibraryCursorProtector>();
        return services;
    }

    private static void ConfigureExternalProviders(
        OpenIddictClientBuilder options,
        IHostEnvironment environment,
        AccountsRuntimeConfiguration runtime)
    {
        options.AllowAuthorizationCodeFlow();
        if (environment.IsDevelopment() && OperatingSystem.IsMacOS())
        {
            // Preview/tunnel authentication must remain safe to run on macOS. Reuse the
            // checkout-local raw keys instead of importing PKCS#12 into the login Keychain.
            options.AddSigningKey(DevelopmentOpenIddictRsaKeyStore.LoadSigningKey(environment))
                .AddEncryptionKey(
                    DevelopmentOpenIddictRsaKeyStore.LoadEncryptionKey(environment));
        }
        else if (environment.IsDevelopment())
        {
            options.AddSigningCertificate(
                    DevelopmentOpenIddictCertificateStore.LoadSigningCertificate(environment))
                .AddEncryptionCertificate(
                    DevelopmentOpenIddictCertificateStore.LoadEncryptionCertificate(environment));
        }
        else
        {
            options.AddSigningCertificates(LoadCertificateSet(
                runtime.Options.SigningCertificatePath,
                runtime.Options.SigningCertificatePassword,
                runtime.Options.PreviousSigningCertificatePath,
                runtime.Options.PreviousSigningCertificatePassword,
                "signing"))
            .AddEncryptionCertificates(LoadCertificateSet(
                runtime.Options.EncryptionCertificatePath,
                runtime.Options.EncryptionCertificatePassword,
                runtime.Options.PreviousEncryptionCertificatePath,
                runtime.Options.PreviousEncryptionCertificatePassword,
                "encryption"));
        }

        options.UseAspNetCore()
            .EnableRedirectionEndpointPassthrough();
        options.UseSystemNetHttp();

        var providers = options.UseWebProviders();
        providers.AddGoogle(google =>
            google.SetClientId(runtime.Options.Google.ClientId)
                .SetClientSecret(runtime.Options.Google.ClientSecret)
                .SetRegistrationId(AuthenticationConstants.GoogleProvider)
                .SetRedirectUri(AuthenticationConstants.GoogleCallbackPath)
                .AddScopes(Scopes.Email, Scopes.Profile));
        providers.AddApple(apple =>
            apple.SetClientId(runtime.Options.Apple.ClientId)
                .SetTeamId(runtime.Options.Apple.TeamId)
                .SetSigningKey(LoadAppleSigningKey(runtime.Options.Apple))
                .SetRegistrationId(AuthenticationConstants.AppleProvider)
                .SetRedirectUri(AuthenticationConstants.AppleCallbackPath)
                .AddScopes(Scopes.Email));
    }

    private static ECDsaSecurityKey LoadAppleSigningKey(AppleProviderOptions options)
    {
        if (!File.Exists(options.PrivateKeyPath))
        {
            throw new InvalidOperationException(
                $"The Apple private key file '{options.PrivateKeyPath}' does not exist.");
        }

        var algorithm = ECDsa.Create();
        try
        {
            // Apple's .p8 is a PEM-encoded PKCS#8 key. Importing it directly keeps the
            // provider credential out of certificate stores and the macOS login Keychain.
            algorithm.ImportFromPem(File.ReadAllText(options.PrivateKeyPath));
            return new ECDsaSecurityKey(algorithm) { KeyId = options.KeyId };
        }
        catch
        {
            algorithm.Dispose();
            throw;
        }
    }

    private static void ConfigureServer(
        OpenIddictServerBuilder options,
        IHostEnvironment environment,
        AccountsRuntimeConfiguration runtime)
    {
        options.SetIssuer(runtime.Issuer)
            .SetAuthorizationEndpointUris("/connect/authorize")
            // OpenIddict accepts every registered URI, while discovery advertises the first.
            // Register the common trailing-slash spelling so replay protection can treat both
            // spellings as the same security boundary.
            .SetTokenEndpointUris("/connect/token", "/connect/token/")
            .AllowAuthorizationCodeFlow()
            .AllowRefreshTokenFlow()
            .RequireProofKeyForCodeExchange()
            .UseReferenceRefreshTokens()
            .SetAccessTokenLifetime(TimeSpan.FromMinutes(10))
            .SetRefreshTokenLifetime(AuthenticationConstants.NativeSessionAbsoluteLifetime)
            .RegisterScopes(
                Scopes.OpenId,
                Scopes.Profile,
                Scopes.OfflineAccess,
                RelistenScopes.UserRead,
                RelistenScopes.LibraryRead,
                RelistenScopes.LibraryWrite,
                RelistenScopes.AccountManage);
        options.Configure(server =>
        {
            server.CodeChallengeMethods.Clear();
            server.CodeChallengeMethods.Add(CodeChallengeMethods.Sha256);
        });

        if (environment.IsDevelopment())
        {
            if (OperatingSystem.IsMacOS())
            {
                // .NET does not support EphemeralKeySet for PKCS#12 on macOS.
                // Raw checkout-local RSA keys avoid certificate and Keychain APIs entirely.
                options.AddSigningKey(
                        DevelopmentOpenIddictRsaKeyStore.LoadSigningKey(environment))
                    .AddEncryptionKey(
                        DevelopmentOpenIddictRsaKeyStore.LoadEncryptionKey(environment));
            }
            else
            {
                options.AddSigningCertificate(
                        DevelopmentOpenIddictCertificateStore.LoadSigningCertificate(environment))
                    .AddEncryptionCertificate(
                        DevelopmentOpenIddictCertificateStore.LoadEncryptionCertificate(environment));
            }
        }
        else
        {
            options.AddSigningCertificates(LoadCertificateSet(
                    runtime.Options.SigningCertificatePath,
                    runtime.Options.SigningCertificatePassword,
                    runtime.Options.PreviousSigningCertificatePath,
                    runtime.Options.PreviousSigningCertificatePassword,
                    "signing"))
                .AddEncryptionCertificates(LoadCertificateSet(
                    runtime.Options.EncryptionCertificatePath,
                    runtime.Options.EncryptionCertificatePassword,
                    runtime.Options.PreviousEncryptionCertificatePath,
                    runtime.Options.PreviousEncryptionCertificatePassword,
                    "encryption"));
        }

        var aspNetCore = options.UseAspNetCore()
            .EnableAuthorizationEndpointPassthrough()
            .EnableTokenEndpointPassthrough();
        if (runtime.AllowLoopbackHttp)
        {
            aspNetCore.DisableTransportSecurityRequirement();
        }
    }

    private static X509Certificate2 LoadCertificate(
        string? path,
        string? password,
        string purpose)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException(
                $"A production {purpose} certificate is required.");
        }

        return X509CertificateLoader.LoadPkcs12FromFile(path, password);
    }

    private static X509Certificate2[] LoadCertificateSet(
        string? currentPath,
        string? currentPassword,
        string? previousPath,
        string? previousPassword,
        string purpose)
    {
        var current = LoadCertificate(currentPath, currentPassword, purpose);
        if (string.IsNullOrWhiteSpace(previousPath))
        {
            if (!string.IsNullOrEmpty(previousPassword))
            {
                throw new InvalidOperationException(
                    $"The previous {purpose} certificate password requires a certificate path.");
            }

            return [current];
        }

        // Keep the current certificate first so Data Protection wraps new keys with it. For
        // OpenIddict, the current certificate must also expire after the previous certificate:
        // OpenIddict selects the valid X.509 credential with the furthest expiration date.
        // Both private keys stay loaded during overlap so old keys and tokens remain usable.
        return
        [
            current,
            LoadCertificate(previousPath, previousPassword, $"previous {purpose}")
        ];
    }

    private static void ConfigureDevelopmentCookie(CookieAuthenticationOptions options)
    {
        options.Cookie.Name = "Relisten.DevelopmentIdentity";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromMinutes(10);
        options.LoginPath = "/development/sign-in";
        options.ReturnUrlParameter = "return_url";
        options.SlidingExpiration = false;
    }

    private static void ConfigureExternalIdentityCookie(CookieAuthenticationOptions options)
    {
        // This is a short bridge between an upstream callback and /connect/authorize,
        // not the mobile session. AuthorizationController deletes it after issuing a code.
        options.Cookie.Name = "__Host-relisten_auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.Path = "/";
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.ExpireTimeSpan = TimeSpan.FromMinutes(10);
        options.SlidingExpiration = false;
    }

    private static void AddPolicy(
        AuthorizationOptions options,
        string name,
        string? scope = null)
    {
        options.AddPolicy(name, policy =>
        {
            policy.AuthenticationSchemes.Add(
                OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
            policy.RequireAuthenticatedUser();
            policy.AddRequirements(new NativeSessionRequirement());
            if (scope is not null)
            {
                policy.AddRequirements(new ScopeRequirement(scope));
            }
        });
    }
}
