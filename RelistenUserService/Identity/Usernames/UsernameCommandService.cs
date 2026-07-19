using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Relisten.Accounts.Contracts.Accounts;
using RelistenUserService.Identity.Entities;
using RelistenUserService.Persistence;

namespace RelistenUserService.Identity.Usernames;

public sealed class UsernameCommandService(
    AccountsDbContext dbContext,
    AdvisoryLockService advisoryLocks,
    UsernameReservationService usernames,
    UsernamePolicy policy,
    TimeProvider timeProvider)
{
    public async Task<UsernameCommandResult> ExecuteAsync(
        Guid userId,
        UpdateUsernameRequest request,
        CancellationToken cancellationToken)
    {
        var canonicalUsername = request.Username?.ToLowerInvariant() ?? "";
        var payloadHash = HashPayload(request, canonicalUsername);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await advisoryLocks.LockAsync(
            $"username-command:{request.ClientCommandUuid:D}",
            cancellationToken);

        // Check the receipt before optimistic concurrency: a retry of a completed command
        // should reproduce its result even though that command advanced username_version.
        var receipt = await dbContext.UsernameCommandReceipts
            .SingleOrDefaultAsync(item => item.Id == request.ClientCommandUuid, cancellationToken);
        if (receipt is not null)
        {
            var user = await RequireUserAsync(userId, cancellationToken);
            if (receipt.UserId != userId
                || !CryptographicOperations.FixedTimeEquals(receipt.PayloadHash, payloadHash))
            {
                return new UsernameCommandResult(UsernameCommandStatus.IdempotencyConflict, user);
            }

            ApplyStoredResult(user, receipt.StoredResult);
            return new UsernameCommandResult(UsernameCommandStatus.Success, user);
        }

        if (!policy.TryNormalize(request.Username, out var requestedUsername))
        {
            var current = await RequireUserAsync(userId, cancellationToken);
            return new UsernameCommandResult(UsernameCommandStatus.InvalidUsername, current);
        }

        var lockedUser = await LockAndReloadUserAsync(userId, cancellationToken);
        if (lockedUser.UsernameVersion != request.ExpectedUsernameVersion)
        {
            return new UsernameCommandResult(
                UsernameCommandStatus.UsernameVersionStale,
                lockedUser);
        }

        var now = timeProvider.GetUtcNow();
        var isFirstReview = lockedUser.UsernameReviewedAt is null;
        var renamed = requestedUsername != lockedUser.Username;
        if (renamed)
        {
            // The generated first username has never been intentionally claimed, so reviewing
            // it neither starts the recurring cooldown nor reserves the discarded default.
            if (!isFirstReview
                && lockedUser.UsernameChangedAt is { } changedAt
                && changedAt + UsernamePolicy.ChangeCooldown > now)
            {
                return new UsernameCommandResult(
                    UsernameCommandStatus.UsernameChangeTooSoon,
                    lockedUser,
                    changedAt + UsernamePolicy.ChangeCooldown);
            }

            await advisoryLocks.LockUsernamesAsync(
                [lockedUser.Username, requestedUsername],
                cancellationToken);
            if (!await usernames.IsAvailableAsync(requestedUsername, now, cancellationToken))
            {
                return new UsernameCommandResult(
                    UsernameCommandStatus.UsernameUnavailable,
                    lockedUser);
            }

            if (!isFirstReview)
            {
                dbContext.UsernameHolds.Add(new UsernameHold
                {
                    Id = Guid.CreateVersion7(),
                    UserId = lockedUser.Id,
                    Username = lockedUser.Username,
                    ReleaseAt = now + UsernamePolicy.ChangeCooldown,
                    CreatedAt = now
                });
            }
            lockedUser.Username = requestedUsername;
            if (!isFirstReview)
            {
                lockedUser.UsernameChangedAt = now;
            }
        }

        if (isFirstReview)
        {
            lockedUser.UsernameReviewedAt = now;
            lockedUser.UsernameVersion++;
        }
        else if (renamed)
        {
            lockedUser.UsernameVersion++;
        }

        lockedUser.UpdatedAt = now;
        var storedResult = StoredUsernameResult.FromUser(lockedUser);
        dbContext.UsernameCommandReceipts.Add(new UsernameCommandReceipt
        {
            Id = request.ClientCommandUuid,
            UserId = userId,
            ExpectedUsernameVersion = request.ExpectedUsernameVersion,
            PayloadHash = payloadHash,
            StoredResult = JsonSerializer.Serialize(storedResult),
            CreatedAt = now
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new UsernameCommandResult(UsernameCommandStatus.Success, lockedUser);
    }

    private Task<User> RequireUserAsync(Guid userId, CancellationToken cancellationToken) =>
        dbContext.Users.SingleAsync(user => user.Id == userId, cancellationToken);

    private async Task<User> LockAndReloadUserAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT id
            FROM identity.users
            WHERE id = {userId}
            FOR UPDATE
            """, cancellationToken);

        // Authentication loads the user into this request's DbContext before the command runs.
        // A raw locking query does not refresh an already tracked entity, so explicitly reload it
        // after acquiring the row lock before checking username_version.
        var user = await RequireUserAsync(userId, cancellationToken);
        await dbContext.Entry(user).ReloadAsync(cancellationToken);
        return user;
    }

    private static byte[] HashPayload(UpdateUsernameRequest request, string username)
    {
        var canonical = string.Join(
            '\n',
            request.ContractVersion.ToString(CultureInfo.InvariantCulture),
            request.ExpectedUsernameVersion.ToString(CultureInfo.InvariantCulture),
            username);
        return SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
    }

    private static void ApplyStoredResult(User user, string storedResult)
    {
        var result = JsonSerializer.Deserialize<StoredUsernameResult>(storedResult)
            ?? throw new InvalidOperationException("A username command receipt has no result.");
        user.Username = result.Username;
        user.UsernameVersion = result.UsernameVersion;
        user.UsernameReviewedAt = result.UsernameReviewedAt;
        user.UsernameChangedAt = result.UsernameChangedAt;
    }

    private sealed record StoredUsernameResult(
        string Username,
        long UsernameVersion,
        DateTimeOffset? UsernameReviewedAt,
        DateTimeOffset? UsernameChangedAt)
    {
        public static StoredUsernameResult FromUser(User user) => new(
            user.Username,
            user.UsernameVersion,
            user.UsernameReviewedAt,
            user.UsernameChangedAt);
    }
}
