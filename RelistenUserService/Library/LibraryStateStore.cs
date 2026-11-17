using Microsoft.EntityFrameworkCore;
using RelistenUserService.Library.Entities;
using RelistenUserService.Persistence;

namespace RelistenUserService.Library;

public sealed class LibraryStateStore(AccountsDbContext dbContext)
{
    public async Task<LibraryState> GetOrCreateForUpdateAsync(
        UserId userId,
        DateTimeOffset now,
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

        return await dbContext
            .LibraryStates.FromSqlInterpolated(
                $"""
                    SELECT *
                    FROM user_data.library_states
                    WHERE user_id = {userId.Value}
                    FOR UPDATE
                """
            )
            .SingleAsync(cancellationToken);
    }
}
