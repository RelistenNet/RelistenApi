using Microsoft.EntityFrameworkCore;
using RelistenUserService.Identity.Entities;
using RelistenUserService.Persistence;
using RelistenUserService.Playlists.Entities;

namespace RelistenUserService.Playlists;

internal static class PlaylistVisibility
{
    // PublicPlaylistDirectory repeats this predicate in SQL; change both together.
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
        CancellationToken cancellationToken,
        bool forFollow = false
    )
    {
        // A follow changes the counter on this row. Lock it for that update from the
        // start so concurrent follows do not deadlock trying to upgrade share locks.
        var source = forFollow
            ? database.Playlists.FromSqlInterpolated(
                $"SELECT * FROM user_data.playlists WHERE public_code = {code} FOR NO KEY UPDATE"
            )
            : database.Playlists.FromSqlInterpolated(
                $"SELECT * FROM user_data.playlists WHERE public_code = {code} FOR SHARE"
            );
        return await source.AsNoTracking().Public().SingleOrDefaultAsync(cancellationToken)
            ?? throw NotFound();
    }
}
