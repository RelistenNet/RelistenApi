using OpenIddict.Abstractions;
using OpenIddict.Server;
using RelistenUserService.Configuration;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace RelistenUserService.Authentication.OpenIdConnect;

internal static class McpOAuth
{
    public const string ClientId = "https://chatgpt.com/oauth/client.json";
    public const string Redirect = "https://chatgpt.com/connector_platform_oauth_redirect";

    private static readonly string[] ResourceScopes =
    [
        RelistenScopes.UserRead,
        RelistenScopes.LibraryRead,
        RelistenScopes.LibraryWrite,
    ];

    /// <summary>
    /// The scopes the MCP resource metadata tells a client it may request. The client's scope
    /// permissions are built from the same list, so the two cannot drift apart. OpenIddict
    /// grants <c>offline_access</c> through the refresh token grant permission instead of a
    /// scope permission.
    /// </summary>
    public static readonly string[] AdvertisedScopes = [.. ResourceScopes, Scopes.OfflineAccess];

    public static void Configure(OpenIddictServerBuilder server, McpConfiguration? mcp)
    {
        if (mcp is not null)
        {
            server
                .RegisterResources(mcp.Resource)
                .SetConfigurationEndpointUris(
                    "/.well-known/openid-configuration",
                    "/.well-known/oauth-authorization-server"
                );
            server.AddEventHandler<HandleConfigurationRequestContext>(handler =>
                handler
                    .SetOrder(
                        OpenIddictServerHandlers.Discovery.AttachAdditionalMetadata.Descriptor.Order
                            + 1
                    )
                    .UseInlineHandler(context =>
                    {
                        // Only the client registered in InitializeAsync is accepted.
                        context.Metadata["client_id_metadata_document_supported"] = true;
                        // ChatGPT chooses its JWT assertion preference when advertised. The
                        // checked-in Relisten clients use PKCE or client secrets.
                        context.TokenEndpointAuthenticationMethods.Remove(
                            ClientAuthenticationMethods.PrivateKeyJwt
                        );
                        context.TokenEndpointAuthenticationMethods.Add("none");
                        return ValueTask.CompletedTask;
                    })
            );
        }

        // Reject a previously registered client when the MCP host is no longer configured.
        server.AddEventHandler<ValidateAuthorizationRequestContext>(handler =>
            handler
                .SetOrder(
                    OpenIddictServerHandlers.Authentication.ValidateResources.Descriptor.Order - 1
                )
                .UseInlineHandler(context =>
                {
                    if (InvalidTarget(context.Request, mcp) is { } error)
                    {
                        context.Reject(error);
                    }
                    return ValueTask.CompletedTask;
                })
        );
        server.AddEventHandler<ValidateTokenRequestContext>(handler =>
            handler
                .SetOrder(OpenIddictServerHandlers.Exchange.ValidateResources.Descriptor.Order - 1)
                .UseInlineHandler(context =>
                {
                    if (InvalidTarget(context.Request, mcp) is { } error)
                    {
                        context.Reject(error);
                    }
                    return ValueTask.CompletedTask;
                })
        );
    }

    private static string? InvalidTarget(OpenIddictRequest request, McpConfiguration? mcp)
    {
        if (request.ClientId != ClientId)
        {
            // A client metadata URL is reserved for MCP. A registration that survives a
            // rollback must never fall through to the first-party native account flow.
            return
                request.ClientId?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) == true
                ? Errors.UnauthorizedClient
                : null;
        }
        if (mcp is null)
        {
            return Errors.UnauthorizedClient;
        }

        var resources = request.GetResources();
        return
            resources.Length == 1
            && resources[0] == mcp.Resource.AbsoluteUri
            && request.GetAudiences().Length == 0
            ? null
            : Errors.InvalidTarget;
    }

    public static async Task InitializeAsync(
        IServiceProvider services,
        McpConfiguration mcp,
        CancellationToken cancellationToken
    )
    {
        await using var scope = services.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var description = new OpenIddictApplicationDescriptor
        {
            ClientId = ClientId,
            ClientType = ClientTypes.Public,
            ConsentType = ConsentTypes.Explicit,
            DisplayName = "ChatGPT",
            RedirectUris = { new Uri(Redirect) },
            Requirements = { Requirements.Features.ProofKeyForCodeExchange },
            Permissions =
            {
                Permissions.Endpoints.Authorization,
                Permissions.Endpoints.Token,
                Permissions.GrantTypes.AuthorizationCode,
                Permissions.GrantTypes.RefreshToken,
                Permissions.ResponseTypes.Code,
                Permissions.Prefixes.Resource + mcp.Resource.AbsoluteUri,
                // An identity scope, so the resource metadata does not advertise it.
                Permissions.Prefixes.Scope + Scopes.Profile,
            },
        };
        description.Permissions.UnionWith(
            ResourceScopes.Select(scope => Permissions.Prefixes.Scope + scope)
        );
        var client = await manager.FindByClientIdAsync(ClientId, cancellationToken);
        if (client is null)
        {
            await manager.CreateAsync(description, cancellationToken);
            return;
        }

        if (
            await manager.GetClientTypeAsync(client, cancellationToken) != description.ClientType
            || await manager.GetConsentTypeAsync(client, cancellationToken)
                != description.ConsentType
            || !description.Permissions.SetEquals(
                await manager.GetPermissionsAsync(client, cancellationToken)
            )
            || !description.Requirements.SetEquals(
                await manager.GetRequirementsAsync(client, cancellationToken)
            )
            || !description
                .RedirectUris.Select(uri => uri.AbsoluteUri)
                .SequenceEqual(await manager.GetRedirectUrisAsync(client, cancellationToken))
        )
        {
            throw new InvalidOperationException(
                "The registered ChatGPT OAuth client does not match the MCP configuration."
            );
        }
    }
}
