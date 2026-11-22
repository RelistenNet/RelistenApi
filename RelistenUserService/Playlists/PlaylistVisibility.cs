using Microsoft.EntityFrameworkCore;
using RelistenUserService.Identity.Entities;
using RelistenUserService.Persistence;
using RelistenUserService.Playlists.Entities;

namespace RelistenUserService.Playlists;

internal static class PlaylistVisibility
{
    internal static IQueryable<Playlist> Public(this IQueryable<Playlist> playlists) =>
        playlists.VisibleTo(null);

    internal static IQueryable<Playlist> VisibleTo(
        this IQueryable<Playlist> playlists,
        UserId? viewer
    ) =>
        playlists.Where(row =>
            row.Owner.Status == UserStatuses.Active
            && (
                (viewer != null && row.OwnerId == viewer)
                || (row.Published && !row.Archived && !row.TakenDown)
            )
        );

    internal static bool IsCode(string code) =>
        code.Length == 32
        && code.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static PlaylistException NotFound() =>
        new(404, "playlist_not_found", "Playlist not found.");

    internal static async Task<Playlist> LockPublicSourceAsync(
        AccountsDbContext database,
        string code,
        CancellationToken cancellationToken
    )
    {
        // Writers and the operator hold the playlist's update lock. Holding a share
        // lock keeps the source public and its structure stable until the command commits.
        return await database
                .Playlists.FromSqlInterpolated(
                    $"""
                    SELECT * FROM user_data.playlists WHERE public_code = {code} FOR SHARE
                    """
                )
                .AsNoTracking()
                .Public()
                .SingleOrDefaultAsync(cancellationToken)
            ?? throw NotFound();
    }
}
