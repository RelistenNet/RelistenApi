using OpenIddict.Abstractions;
using RelistenUserService.Configuration;
using RelistenUserService.Persistence;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace RelistenUserService.Authentication;

public sealed class DevelopmentDatabaseInitializer(
    IServiceProvider serviceProvider,
    AccountsRuntimeConfiguration runtime)
{
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        await SeedScopesAsync(scope.ServiceProvider, cancellationToken);
        await SeedClientAsync(
            scope.ServiceProvider,
            "relisten-mobile-ios-dev",
            "Relisten iOS development",
            "net.relisten.mobile:/oauth2redirect/ios",
            cancellationToken);
        await SeedClientAsync(
            scope.ServiceProvider,
            "relisten-mobile-android-dev",
            "Relisten Android development",
            "net.relisten.mobile:/oauth2redirect/android",
            cancellationToken);
    }

    private async Task SeedScopesAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var manager = services.GetRequiredService<IOpenIddictScopeManager>();
        foreach (var name in RelistenScopes.Native)
        {
            var descriptor = new OpenIddictScopeDescriptor { Name = name };
            descriptor.Resources.Add(runtime.Options.Audience);
            var existing = await manager.FindByNameAsync(name, cancellationToken);
            if (existing is null)
            {
                await manager.CreateAsync(descriptor, cancellationToken);
            }
            else
            {
                await manager.UpdateAsync(existing, descriptor, cancellationToken);
            }
        }
    }

    private static async Task SeedClientAsync(
        IServiceProvider services,
        string clientId,
        string displayName,
        string redirectUri,
        CancellationToken cancellationToken)
    {
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            ClientType = ClientTypes.Public,
            ConsentType = ConsentTypes.Implicit,
            DisplayName = displayName
        };
        descriptor.RedirectUris.Add(new Uri(redirectUri));
        descriptor.Permissions.UnionWith(
        [
            Permissions.Endpoints.Authorization,
            Permissions.Endpoints.Token,
            Permissions.GrantTypes.AuthorizationCode,
            Permissions.GrantTypes.RefreshToken,
            Permissions.ResponseTypes.Code,
            Permissions.Prefixes.Scope + Scopes.Profile,
            .. RelistenScopes.Native.Select(scope => Permissions.Prefixes.Scope + scope)
        ]);
        descriptor.Requirements.Add(Requirements.Features.ProofKeyForCodeExchange);

        var manager = services.GetRequiredService<IOpenIddictApplicationManager>();
        var existing = await manager.FindByClientIdAsync(clientId, cancellationToken);
        if (existing is null)
        {
            await manager.CreateAsync(descriptor, cancellationToken);
        }
        else
        {
            await manager.UpdateAsync(existing, descriptor, cancellationToken);
        }
    }
}
