using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace RelistenUserService.Persistence;

public sealed class AdvisoryLockService(AccountsDbContext dbContext)
{
    public Task LockAsync(string resource, CancellationToken cancellationToken) =>
        LockAllAsync([resource], cancellationToken);

    public async Task LockAllAsync(
        IEnumerable<string> resources,
        CancellationToken cancellationToken)
    {
        var orderedKeys = resources
            .Select(CreateKey)
            .Distinct()
            .Order()
            .ToArray();
        if (orderedKeys.Length == 0)
        {
            return;
        }

        // Lock the numeric PostgreSQL keys in one globally consistent order. Besides saving up to
        // 1,000 round trips for a favorites batch, ordering the actual keys (rather than their source
        // strings) keeps overlapping lock sets from acquiring the same advisory locks in reverse.
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT pg_advisory_xact_lock(lock_key)
            FROM unnest({orderedKeys}) AS requested_locks(lock_key)
            ORDER BY lock_key
            """, cancellationToken);
    }

    public Task LockUsernamesAsync(
        IEnumerable<string> usernames,
        CancellationToken cancellationToken) =>
        LockAllAsync(
            usernames.Select(username => $"username:{username}"),
            cancellationToken);

    private static long CreateKey(string resource)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(resource));
        return BinaryPrimitives.ReadInt64BigEndian(hash);
    }
}
