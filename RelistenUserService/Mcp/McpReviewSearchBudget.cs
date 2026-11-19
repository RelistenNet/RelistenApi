using System.Threading.RateLimiting;

namespace RelistenUserService.Mcp;

/// <summary>A signed-in listener gets an independent review budget before the catalog call.</summary>
internal sealed class McpReviewSearchBudget(string? catalogKey) : IDisposable
{
    private readonly PartitionedRateLimiter<UserId> accounts = PartitionedRateLimiter.Create<
        UserId,
        UserId
    >(account =>
        RateLimitPartition.GetFixedWindowLimiter(
            account,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1),
            }
        )
    );

    public string Acquire(UserId account)
    {
        using var lease = accounts.AttemptAcquire(account);
        if (!lease.IsAcquired)
        {
            throw new McpCatalogException(
                "Your signed-in review search limit was reached. Try again in a minute."
            );
        }

        // Send the service credential only for this route. The access token and
        // listener ID stay in the User Service.
        return catalogKey
            ?? throw new InvalidOperationException(
                "Authenticated review search is not configured."
            );
    }

    public void Dispose() => accounts.Dispose();
}
