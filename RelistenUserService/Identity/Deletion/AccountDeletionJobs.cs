using Microsoft.EntityFrameworkCore;
using RelistenUserService.Identity.Entities;
using RelistenUserService.Persistence;

namespace RelistenUserService.Identity.Deletion;

public sealed class AccountDeletionJobs(AccountsDbContext database, TimeProvider clock)
{
    /// <summary>
    /// Commits the account's deletion job before a worker can be started; a repeated
    /// request returns the same job. Returns null if the account is inactive.
    /// </summary>
    public async Task<Guid?> RequestAsync(UserId userId, CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(
            cancellationToken
        );

        var statuses = await database
            .Database.SqlQuery<string>(
                $"""
                SELECT status FROM identity.users WHERE id = {userId.ToString()} FOR UPDATE
                """
            )
            .ToListAsync(cancellationToken);
        if (statuses.SingleOrDefault() != UserStatuses.Active)
        {
            return null;
        }

        var job = await database.AccountDeletionJobs.SingleOrDefaultAsync(
            existing => existing.UserId == userId,
            cancellationToken
        );
        if (job is null)
        {
            job = new AccountDeletionJob
            {
                Id = Guid.CreateVersion7(),
                UserId = userId,
                RequestedAt = clock.GetUtcNow(),
            };
            database.AccountDeletionJobs.Add(job);
            await database.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return job.Id;
    }
}
