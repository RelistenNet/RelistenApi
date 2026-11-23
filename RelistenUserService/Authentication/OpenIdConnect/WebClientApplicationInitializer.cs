using OpenIddict.Abstractions;
using OpenIddict.Core;
using OpenIddict.EntityFrameworkCore.Models;
using RelistenUserService.Configuration;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace RelistenUserService.Authentication.OpenIdConnect;

public sealed class WebClientApplicationInitializer(
    IServiceProvider serviceProvider,
    AccountsRuntimeConfiguration runtime
)
{
    private const int MaximumUpgradeAttempts = 3;

    private static readonly HashSet<string> ExpectedPermissions =
    [
        Permissions.Endpoints.Authorization,
        Permissions.Endpoints.Token,
        Permissions.GrantTypes.AuthorizationCode,
        Permissions.ResponseTypes.Code,
        Permissions.Prefixes.Scope + Scopes.Profile,
    ];

    private static readonly HashSet<string> ExpectedRequirements =
    [
        Requirements.Features.ProofKeyForCodeExchange,
    ];

    /// <summary>Every fixed callback the persisted client may carry, configured or not.</summary>
    private static readonly HashSet<string> FixedRedirectUris =
    [
        AuthenticationConstants.CanonicalWebCallback,
        AuthenticationConstants.LocalWebCallback,
        AuthenticationConstants.StagingWebCallback,
    ];

    /// <summary>
    /// The fixed callbacks the persisted client must carry. The staging callback is
    /// required only while its origin is configured, so deploying this image changes
    /// nothing until Accounts:WebOrigins names the staging origin.
    /// </summary>
    internal static HashSet<string> RequiredRedirectUris(AccountsRuntimeConfiguration runtime)
    {
        HashSet<string> required =
        [
            AuthenticationConstants.CanonicalWebCallback,
            AuthenticationConstants.LocalWebCallback,
        ];
        if (
            runtime.Options.WebOrigins.Contains(
                AuthenticationConstants.StagingWebOrigin,
                StringComparer.Ordinal
            )
        )
        {
            required.Add(AuthenticationConstants.StagingWebCallback);
        }

        return required;
    }

    public Task InitializeAsync(CancellationToken cancellationToken) =>
        InitializeAsync(beforeFirstWrite: null, cancellationToken);

    /// <param name="beforeFirstWrite">
    /// Tests only: runs between the first read and write, to stage a conflicting update.
    /// </param>
    internal async Task InitializeAsync(
        Func<Task>? beforeFirstWrite,
        CancellationToken cancellationToken
    )
    {
        // Startup runs before the preview reload service, but serialize with it anyway:
        // both rewrite this client's redirect URIs.
        await runtime.BrowserOrigins.SessionCreationAndReload.WaitAsync(cancellationToken);
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                // A fresh scope per attempt. A failed update leaves its changes on the
                // tracked entity, and the EF store hands that entity back on the next
                // lookup, so reusing the context would re-read our own unsaved write.
                await using var scope = serviceProvider.CreateAsyncScope();
                var manager =
                    scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
                var existing = await manager.FindByClientIdAsync(
                    AuthenticationConstants.WebClientId,
                    cancellationToken
                );
                if (existing is null)
                {
                    await manager.CreateAsync(Descriptor(), cancellationToken);
                    return;
                }

                if (attempt == 1 && beforeFirstWrite is not null)
                {
                    await beforeFirstWrite();
                }

                try
                {
                    await AddMissingFixedRedirectUrisAsync(
                        manager,
                        existing,
                        runtime,
                        cancellationToken
                    );
                    break;
                }
                // Another replica updated the client between our read and write. Re-read
                // and re-check rather than overwrite its change.
                catch (OpenIddictExceptions.ConcurrencyException)
                {
                    // The cache may hold the entity with our failed change applied.
                    await EvictAsync(scope, existing);
                    if (attempt >= MaximumUpgradeAttempts)
                    {
                        throw;
                    }
                }
            }

            await using var verify = serviceProvider.CreateAsyncScope();
            var verifier =
                verify.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
            var current =
                await verifier.FindByClientIdAsync(
                    AuthenticationConstants.WebClientId,
                    cancellationToken
                )
                ?? throw new InvalidOperationException(
                    "The relisten-web client disappeared during startup."
                );
            await ValidateAsync(verifier, current, runtime, cancellationToken);
        }
        finally
        {
            runtime.BrowserOrigins.SessionCreationAndReload.Release();
        }
    }

    private static Task EvictAsync(AsyncServiceScope scope, object application) =>
        scope
            .ServiceProvider.GetRequiredService<
                IOpenIddictApplicationCache<OpenIddictEntityFrameworkCoreApplication<Guid>>
            >()
            .RemoveAsync(
                (OpenIddictEntityFrameworkCoreApplication<Guid>)application,
                CancellationToken.None
            )
            .AsTask();

    /// <summary>
    /// Adds a newly configured fixed callback (such as staging) to a client persisted
    /// before it existed. The client must otherwise match the browser boundary; preview
    /// callbacks and the secret are preserved.
    /// </summary>
    internal static async Task AddMissingFixedRedirectUrisAsync(
        IOpenIddictApplicationManager manager,
        object application,
        AccountsRuntimeConfiguration runtime,
        CancellationToken cancellationToken
    )
    {
        await ValidateCoreAsync(manager, application, runtime, cancellationToken);
        var redirectUris = await manager.GetRedirectUrisAsync(application, cancellationToken);
        var missing = RequiredRedirectUris(runtime).Except(redirectUris).ToArray();
        if (missing.Length == 0)
        {
            return;
        }

        var descriptor = new OpenIddictApplicationDescriptor();
        await manager.PopulateAsync(descriptor, application, cancellationToken);
        descriptor.RedirectUris.UnionWith(missing.Select(value => new Uri(value)));
        await manager.UpdateAsync(application, descriptor, cancellationToken);
    }

    internal static async Task ValidateAsync(
        IOpenIddictApplicationManager manager,
        object application,
        AccountsRuntimeConfiguration runtime,
        CancellationToken cancellationToken
    )
    {
        await ValidateCoreAsync(manager, application, runtime, cancellationToken);
        var redirectUris = await manager.GetRedirectUrisAsync(application, cancellationToken);
        if (!RequiredRedirectUris(runtime).IsSubsetOf(redirectUris))
        {
            throw MismatchedClient();
        }
    }

    /// <summary>Everything except the presence of the required fixed callbacks.</summary>
    private static async Task ValidateCoreAsync(
        IOpenIddictApplicationManager manager,
        object application,
        AccountsRuntimeConfiguration runtime,
        CancellationToken cancellationToken
    )
    {
        var validSecret = await manager.ValidateClientSecretAsync(
            application,
            runtime.Options.WebClientSecret,
            cancellationToken
        );
        var clientType = await manager.GetClientTypeAsync(application, cancellationToken);
        var permissions = await manager.GetPermissionsAsync(application, cancellationToken);
        var requirements = await manager.GetRequirementsAsync(application, cancellationToken);
        var redirectUris = await manager.GetRedirectUrisAsync(application, cancellationToken);
        if (
            !validSecret
            || clientType != ClientTypes.Confidential
            || !permissions.ToHashSet(StringComparer.Ordinal).SetEquals(ExpectedPermissions)
            || !requirements.ToHashSet(StringComparer.Ordinal).SetEquals(ExpectedRequirements)
            || redirectUris.Any(uri =>
                !FixedRedirectUris.Contains(uri) && !WebOriginRegistry.IsPreviewCallback(uri)
            )
        )
        {
            throw MismatchedClient();
        }
    }

    private static InvalidOperationException MismatchedClient() =>
        new("The persisted relisten-web client does not match the configured browser boundary.");

    private OpenIddictApplicationDescriptor Descriptor()
    {
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = AuthenticationConstants.WebClientId,
            ClientSecret = runtime.Options.WebClientSecret,
            ClientType = ClientTypes.Confidential,
            ConsentType = ConsentTypes.Implicit,
            DisplayName = "Relisten web",
        };
        descriptor.Permissions.UnionWith(ExpectedPermissions);
        descriptor.Requirements.UnionWith(ExpectedRequirements);
        descriptor.RedirectUris.UnionWith(
            RequiredRedirectUris(runtime).Select(value => new Uri(value))
        );
        return descriptor;
    }
}
