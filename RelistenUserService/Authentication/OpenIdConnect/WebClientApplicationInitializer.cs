using OpenIddict.Abstractions;
using RelistenUserService.Configuration;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace RelistenUserService.Authentication.OpenIdConnect;

public sealed class WebClientApplicationInitializer(
    IServiceProvider serviceProvider,
    AccountsRuntimeConfiguration runtime
)
{
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

    private static readonly HashSet<string> ExpectedRedirectUris =
    [
        AuthenticationConstants.CanonicalWebCallback,
        AuthenticationConstants.LocalWebCallback,
    ];

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var existing = await manager.FindByClientIdAsync(
            AuthenticationConstants.WebClientId,
            cancellationToken
        );
        if (existing is null)
        {
            await manager.CreateAsync(Descriptor(), cancellationToken);
            return;
        }

        await ValidateAsync(manager, existing, runtime, cancellationToken);
    }

    internal static async Task ValidateAsync(
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
            || !ExpectedRedirectUris.IsSubsetOf(redirectUris)
            || redirectUris.Any(uri =>
                !ExpectedRedirectUris.Contains(uri) && !WebOriginRegistry.IsPreviewCallback(uri)
            )
        )
        {
            throw new InvalidOperationException(
                "The persisted relisten-web client does not match the configured browser boundary."
            );
        }
    }

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
        descriptor.RedirectUris.UnionWith(ExpectedRedirectUris.Select(value => new Uri(value)));
        return descriptor;
    }
}
