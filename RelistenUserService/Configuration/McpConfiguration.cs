namespace RelistenUserService.Configuration;

public sealed record McpConfiguration(
    Uri Resource,
    Uri Catalog,
    bool EnableAccountTools,
    IReadOnlySet<UserId> AllowedAccounts
)
{
    public bool HasAccountTools => EnableAccountTools || AllowedAccounts.Count > 0;

    public bool Allows(UserId userId) => EnableAccountTools || AllowedAccounts.Contains(userId);

    public static McpConfiguration? Create(AccountsOptions options, IHostEnvironment environment)
    {
        if (string.IsNullOrEmpty(options.McpHost))
        {
            return null;
        }

        if (
            !Uri.TryCreate($"https://{options.McpHost}/mcp", UriKind.Absolute, out var resource)
            || resource.Authority != options.McpHost
            || resource.AbsolutePath != "/mcp"
            || resource.UserInfo.Length > 0
            || resource.Query.Length > 0
            || resource.Fragment.Length > 0
            || options.McpHost == options.AuthHost
            || options.McpHost == options.AccountsHost
            || options.WebOrigins.Any(origin => new Uri(origin).Authority == resource.Authority)
            || (environment.IsProduction() && options.McpHost != "mcp.relisten.net")
            || (options.McpResourceUri is { } configured && configured != resource.AbsoluteUri)
            || options.Audience == resource.AbsoluteUri
        )
        {
            throw new InvalidOperationException(
                "Accounts:McpHost and McpResourceUri must identify the dedicated HTTPS MCP endpoint."
            );
        }

        if (
            !Uri.TryCreate(options.McpCatalogBaseUrl, UriKind.Absolute, out var catalog)
            || (
                catalog.Scheme != Uri.UriSchemeHttps
                && !(
                    environment.IsDevelopment()
                    && catalog.IsLoopback
                    && catalog.Scheme == Uri.UriSchemeHttp
                )
            )
            || catalog.UserInfo.Length > 0
            || catalog.Query.Length > 0
            || catalog.Fragment.Length > 0
            || catalog.AbsolutePath != "/"
        )
        {
            throw new InvalidOperationException(
                "Accounts:McpCatalogBaseUrl must be the catalog origin."
            );
        }

        return new(
            resource,
            catalog,
            options.McpAccountTools,
            options.McpAccountAllowlist.Select(UserId.Parse).ToHashSet()
        );
    }
}
