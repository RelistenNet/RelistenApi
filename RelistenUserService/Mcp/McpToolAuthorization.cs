using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using OpenIddict.Abstractions;
using RelistenUserService.Authentication.OpenIdConnect;
using RelistenUserService.Configuration;
using RelistenUserService.Identity.Entities;

namespace RelistenUserService.Mcp;

internal sealed class McpToolAuthorization(
    IHttpContextAccessor http,
    AccountsRuntimeConfiguration runtime,
    McpAccountAuthorization accounts
)
{
    public Task<NativeSession?> AuthenticateAsync(CancellationToken cancellationToken) =>
        accounts.AuthenticateAsync(http.HttpContext!.User, cancellationToken);

    public async Task<(NativeSession? Session, CallToolResult? Error)> AuthorizeAsync(
        string scope,
        CancellationToken cancellationToken
    )
    {
        var principal = http.HttpContext!.User;
        var session = await AuthenticateAsync(cancellationToken);
        if (session is null)
        {
            return (
                null,
                Error("invalid_token", "Connect your Relisten account to use this tool.", scope)
            );
        }
        if (!principal.HasScope(scope))
        {
            return (
                null,
                Error(
                    "insufficient_scope",
                    "Grant this permission in Relisten to use this tool.",
                    scope
                )
            );
        }

        return (session, null);
    }

    private CallToolResult Error(string code, string message, string scope)
    {
        var result = McpResults.Error(message);
        result.Meta = new JsonObject
        {
            ["mcp/www_authenticate"] = new JsonArray(Challenge(runtime.Mcp!, code, message, scope)),
        };
        return result;
    }

    internal static string Challenge(
        McpConfiguration mcp,
        string code,
        string message,
        string? scope = null
    )
    {
        var metadata = new Uri(mcp.Resource, McpEndpoints.MetadataPath);
        return $"Bearer resource_metadata=\"{metadata.AbsoluteUri}\", error=\"{code}\", error_description=\"{message}\""
            + (scope is null ? "" : $", scope=\"{scope}\"");
    }
}
