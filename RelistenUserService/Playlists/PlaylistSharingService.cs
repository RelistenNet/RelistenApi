using System.Data;
using Microsoft.EntityFrameworkCore;
using Relisten.Accounts.Contracts.Playlists;
using RelistenUserService.Library;
using RelistenUserService.Persistence;
using RelistenUserService.Playlists.Entities;

namespace RelistenUserService.Playlists;

public sealed class PlaylistSharingService(
    AccountsDbContext database,
    LibraryStateStore stateStore,
    TimeProvider clock
)
{
    private const int MaximumFollows = 500;
    private const int ReportsPerMinute = 10;

    public async Task<PublicPlaylistSnapshot?> SnapshotAsync(
        string code,
        CancellationToken cancellationToken
    )
    {
        if (!PlaylistVisibility.IsCode(code))
        {
            return null;
        }

        await using var transaction = await database.Database.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken
        );
        var head = await database
            .Playlists.AsNoTracking()
            .Public()
            .Where(row => row.PublicCode == code)
            .Select(row => new
            {
                row.Id,
                row.Revision,
                Owner = new PublicPlaylistOwner(row.OwnerId, row.Owner.Username),
                Metadata = new PlaylistMetadata(row.Name, row.Description),
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (head is null)
        {
            return null;
        }

        var generation = await database
            .HistoryGenerations.Select(row => row.Value)
            .SingleAsync(cancellationToken);
        var structure = await PlaylistReadService.ReadStructureAsync(
            database,
            head.Id,
            cancellationToken
        );
        await transaction.CommitAsync(cancellationToken);
        return new(
            1,
            generation,
            head.Id,
            code,
            head.Revision,
            head.Owner,
            head.Metadata,
            structure
        );
    }

    public async Task FollowAsync(UserId actor, string code, CancellationToken cancellationToken)
    {
        CheckPublicCode(code);
        await using var transaction = await database.Database.BeginTransactionAsync(
            cancellationToken
        );
        await PlaylistAccountLock.RequireActiveAsync(database, actor, cancellationToken);
        var now = clock.GetUtcNow();
        var state = await stateStore.GetOrCreateForUpdateAsync(actor, now, cancellationToken);
        var source = await PlaylistVisibility.LockPublicSourceAsync(
            database,
            code,
            cancellationToken
        );

        // The owner already has the playlist in `playlists`; a follow would list it twice.
        if (source.OwnerId == actor)
        {
            throw new PlaylistException(
                422,
                "own_playlist",
                "You cannot follow your own playlist."
            );
        }

        var follows = database.PlaylistFollows.Where(row => row.UserId == actor);
        if (!await follows.AnyAsync(row => row.PlaylistId == source.Id, cancellationToken))
        {
            if (await follows.CountAsync(cancellationToken) >= MaximumFollows)
            {
                throw new PlaylistException(
                    422,
                    "quota_exceeded",
                    "An account can follow at most 500 playlists."
                );
            }

            database.PlaylistFollows.Add(
                new()
                {
                    UserId = actor,
                    PlaylistId = source.Id,
                    CreatedAt = now,
                }
            );
            AppendMembership(state, source.Id, "playlist_followed", now);
            await database.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task UnfollowAsync(
        UserId actor,
        PlaylistId playlistId,
        CancellationToken cancellationToken
    )
    {
        await using var transaction = await database.Database.BeginTransactionAsync(
            cancellationToken
        );
        await PlaylistAccountLock.RequireActiveAsync(database, actor, cancellationToken);
        var now = clock.GetUtcNow();
        var state = await stateStore.GetOrCreateForUpdateAsync(actor, now, cancellationToken);
        var follow = await database.PlaylistFollows.FindAsync(
            [actor, playlistId],
            cancellationToken
        );

        if (follow is not null)
        {
            database.PlaylistFollows.Remove(follow);
            AppendMembership(state, playlistId, "playlist_unfollowed", now);
            await database.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task ReportAsync(
        UserId actor,
        string code,
        string reason,
        CancellationToken cancellationToken
    )
    {
        CheckPublicCode(code);
        reason = PlaylistValidation.NormalizeReportReason(reason);

        await using var transaction = await database.Database.BeginTransactionAsync(
            cancellationToken
        );
        await PlaylistAccountLock.RequireActiveAsync(database, actor, cancellationToken);
        var now = clock.GetUtcNow();
        // Serialize distinct reports by account, including across service replicas.
        await stateStore.GetOrCreateForUpdateAsync(actor, now, cancellationToken);
        var source = await PlaylistVisibility.LockPublicSourceAsync(
            database,
            code,
            cancellationToken
        );
        var reports = database.PlaylistReports.Where(row => row.UserId == actor);
        var alreadyOpen = await reports.AnyAsync(
            row => row.PlaylistId == source.Id && row.ClosedAt == null,
            cancellationToken
        );
        if (!alreadyOpen)
        {
            if (
                await reports.CountAsync(
                    row => row.CreatedAt > now.AddMinutes(-1),
                    cancellationToken
                ) >= ReportsPerMinute
            )
            {
                throw new PlaylistException(
                    429,
                    "playlist_report_rate_limited",
                    "Wait before reporting another playlist."
                );
            }

            database.PlaylistReports.Add(
                new()
                {
                    Id = PlaylistReportId.New(),
                    UserId = actor,
                    PlaylistId = source.Id,
                    Reason = reason,
                    CreatedAt = now,
                }
            );
            await database.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static void CheckPublicCode(string code)
    {
        if (!PlaylistVisibility.IsCode(code))
        {
            throw PlaylistVisibility.NotFound();
        }
    }

    private void AppendMembership(
        Library.Entities.LibraryState state,
        PlaylistId playlistId,
        string changeType,
        DateTimeOffset now
    ) =>
        stateStore.Append(
            state,
            new()
            {
                ChangeType = changeType,
                FollowedPlaylistId = playlistId,
                ChangedAt = now,
            }
        );
}
