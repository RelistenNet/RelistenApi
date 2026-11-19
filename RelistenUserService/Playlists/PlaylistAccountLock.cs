using Microsoft.EntityFrameworkCore;
using RelistenUserService.Identity.Entities;
using RelistenUserService.Persistence;

namespace RelistenUserService.Playlists;

internal static class PlaylistAccountLock
{
    // Account purge takes FOR UPDATE. Acquire this before any library or playlist row
    // lock so an authenticated write either commits before purge or sees the account gone.
    // Advisory locks on operation IDs may come first; purge never takes them.
    internal static Task<bool> AcquireAsync(
        AccountsDbContext database,
        UserId actor,
        bool requireActive,
        CancellationToken cancellationToken
    ) =>
        database
            .Database.SqlQuery<bool>(
                $"""
                SELECT TRUE AS "Value" FROM identity.users
                WHERE id = {actor.Value} AND (NOT {requireActive} OR status = {UserStatuses.Active})
                FOR SHARE
                """
            )
            .SingleOrDefaultAsync(cancellationToken);

    internal static async Task RequireActiveAsync(
        AccountsDbContext database,
        UserId actor,
        CancellationToken cancellationToken
    )
    {
        if (!await AcquireAsync(database, actor, requireActive: true, cancellationToken))
        {
            throw new PlaylistException(401, "unauthorized", "Sign in as an active account.");
        }
    }
}
