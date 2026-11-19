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
            return null;
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
                Permissions.Prefixes.Scope + Scopes.Profile,
                Permissions.Prefixes.Scope + RelistenScopes.UserRead,
                Permissions.Prefixes.Scope + RelistenScopes.LibraryRead,
                Permissions.Prefixes.Scope + RelistenScopes.LibraryWrite,
            },
        };
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
