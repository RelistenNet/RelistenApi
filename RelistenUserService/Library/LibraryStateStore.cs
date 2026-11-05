using Microsoft.EntityFrameworkCore;
using RelistenUserService.Library.Entities;
using RelistenUserService.Persistence;

namespace RelistenUserService.Library;

public enum LibraryStateLockMode
{
    SharedRead,
    ExclusiveWrite,
}

public sealed class LibraryStateStore(AccountsDbContext dbContext)
{
    public async Task<LibraryState> GetOrCreateAsync(
        UserId userId,
        DateTimeOffset now,
        LibraryStateLockMode lockMode,
        CancellationToken cancellationToken
    )
    {
        var stateId = Guid.CreateVersion7();
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO user_data.library_states
                (id, user_id, revision, created_at, updated_at)
            VALUES ({stateId}, {userId.Value}, 0, {now}, {now})
            ON CONFLICT (user_id) DO NOTHING
            """,
            cancellationToken
        );

        return lockMode == LibraryStateLockMode.ExclusiveWrite
            ? await dbContext
                .LibraryStates.FromSqlInterpolated(
                    $"""
                        SELECT *
                        FROM user_data.library_states
                        WHERE user_id = {userId.Value}
                        FOR UPDATE
                    """
                )
                .SingleAsync(cancellationToken)
            : await dbContext
                .LibraryStates.FromSqlInterpolated(
                    $"""
                    SELECT *
                    FROM user_data.library_states
                    WHERE user_id = {userId.Value}
                    FOR SHARE
                    """
                )
                .SingleAsync(cancellationToken);
    }
}
