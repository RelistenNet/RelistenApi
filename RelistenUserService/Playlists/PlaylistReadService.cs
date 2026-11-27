using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Relisten.Accounts.Contracts.Playlists;
using RelistenUserService.Persistence;
using RelistenUserService.Playlists.Entities;

namespace RelistenUserService.Playlists;

public sealed class PlaylistReadService(AccountsDbContext dbContext)
{
    public async Task<PlaylistList> ListAsync(
        UserId actor,
        string view,
        CancellationToken cancellationToken
    )
    {
        if (view is not ("active" or "archived"))
        {
            throw new PlaylistException(
                422,
                "invalid_playlist_operation",
                "view must be active or archived."
            );
        }
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken
        );
        var generation = await GenerationAsync(cancellationToken);
        var playlists = dbContext
            .Playlists.AsNoTracking()
            .Where(row => row.OwnerId == actor && row.Archived == (view == "archived"))
            .OrderByDescending(row => row.UpdatedAt)
            .ThenBy(row => row.Id);
        var summaries = await ReadSummariesAsync(dbContext, playlists, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(1, generation, summaries);
    }

    internal static async Task<PlaylistSummary[]> ReadSummariesAsync(
        AccountsDbContext database,
        IQueryable<Playlist> playlists,
        CancellationToken ct
    )
    {
        var summaries = await Summaries(playlists).ToArrayAsync(ct);
        var popularity = await PlaylistPopularity.ReadAsync(
            database,
            summaries.Where(row => row.FollowerCount.HasValue).Select(row => row.PlaylistId),
            ct
        );
        return summaries
            .Select(row =>
                popularity.TryGetValue(row.PlaylistId.Value, out var snapshot)
                    ? row with
                    {
                        Popularity = snapshot.Resource,
                        PopularityAsOf = snapshot.AsOf,
                    }
                    : row
            )
            .ToArray();
    }

    internal static IQueryable<PlaylistSummary> Summaries(IQueryable<Playlist> playlists) =>
        playlists.Select(row => new PlaylistSummary(
            row.Id,
            row.Name,
            row.EntryCount,
            row.Archived,
            row.Revision,
            row.UpdatedAt
        )
        {
            TrackCount = row.TrackCount,
            Description = row.Description,
            PublicCode = row.PublicCode,
            Published = row.Published,
            TakenDown = row.TakenDown,
            FollowerCount =
                row.Published && !row.Archived && !row.TakenDown ? row.FollowerCount : null,
        });

    internal static async Task<FollowedPlaylistHead[]> FollowedHeadsAsync(
        AccountsDbContext database,
        UserId actor,
        CancellationToken cancellationToken
    )
    {
        var visible = database.Playlists.AsNoTracking().Public();
        var heads = await (
            from follow in database.PlaylistFollows.AsNoTracking()
            where follow.UserId == actor
            join playlist in visible on follow.PlaylistId equals playlist.Id into matches
            from playlist in matches.DefaultIfEmpty()
            orderby follow.PlaylistId
            select new FollowedPlaylistHead(follow.PlaylistId, playlist != null)
            {
                PublicCode = playlist == null ? null : playlist.PublicCode,
                Name = playlist == null ? null : playlist.Name,
                Owner = playlist == null ? null : new(playlist.OwnerId, playlist.Owner.Username),
                Revision = playlist == null ? null : playlist.Revision,
                FollowerCount = playlist == null ? null : playlist.FollowerCount,
            }
        ).ToArrayAsync(cancellationToken);
        var popularity = await PlaylistPopularity.ReadAsync(
            database,
            heads.Where(row => row.Available).Select(row => row.PlaylistId),
            cancellationToken
        );
        return heads
            .Select(row =>
                popularity.TryGetValue(row.PlaylistId.Value, out var snapshot)
                    ? row with
                    {
                        Popularity = snapshot.Resource,
                        PopularityAsOf = snapshot.AsOf,
                    }
                    : row
            )
            .ToArray();
    }

    public async Task<PlaylistSnapshot> SnapshotAsync(
        UserId actor,
        PlaylistId id,
        CancellationToken cancellationToken
    )
    {
        // Repeatable read binds the head, structure and generation to one database snapshot
        // without holding the writer's playlist/library locks across multiple queries.
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken
        );
        var playlist = await AuthorizedPlaylistAsync(actor, id, cancellationToken);
        var generation = await GenerationAsync(cancellationToken);
        var structure = await ReadStructureAsync(dbContext, id, cancellationToken);
        var isPublic = playlist.Published && !playlist.Archived && !playlist.TakenDown;
        var popularity = await PlaylistPopularity.ReadAsync(
            dbContext,
            isPublic ? [id] : [],
            cancellationToken
        );
        var snapshot = popularity.GetValueOrDefault(id.Value);
        await transaction.CommitAsync(cancellationToken);

        return new(
            1,
            generation,
            id,
            playlist.Revision,
            playlist.Archived,
            "owner",
            new(playlist.Name, playlist.Description),
            structure,
            playlist.CreatedAt,
            playlist.UpdatedAt
        )
        {
            PublicCode = playlist.PublicCode,
            Published = playlist.Published,
            TakenDown = playlist.TakenDown,
            FollowerCount = isPublic ? playlist.FollowerCount : null,
            Popularity = snapshot?.Resource,
            PopularityAsOf = snapshot?.AsOf,
        };
    }

    internal static async Task<PlaylistSegmentSnapshot[]> ReadStructureAsync(
        AccountsDbContext dbContext,
        PlaylistId id,
        CancellationToken cancellationToken
    )
    {
        var segments = await dbContext
            .PlaylistSegments.AsNoTracking()
            .Where(row => row.PlaylistId == id && !row.Deleted)
            .OrderBy(row => row.Rank)
            .ToArrayAsync(cancellationToken);
        var entries = await dbContext
            .PlaylistEntries.AsNoTracking()
            .Where(row => row.PlaylistId == id && !row.Deleted)
            .OrderBy(row => row.Rank)
            .ToArrayAsync(cancellationToken);
        var entriesBySegment = entries.ToLookup(row => row.SegmentId);

        return segments
            .Select(segment => new PlaylistSegmentSnapshot(
                segment.Id,
                segment.Rank!,
                entriesBySegment[segment.Id]
                    .Select(entry => new PlaylistEntrySnapshot(
                        entry.Id,
                        entry.TrackId,
                        entry.Rank!,
                        entry.Description
                    ))
                    .ToArray(),
                segment.Description
            ))
            .ToArray();
    }

    public async Task<PlaylistChanges> ChangesAsync(
        UserId actor,
        PlaylistId id,
        long afterRevision,
        CancellationToken cancellationToken
    )
    {
        if (afterRevision < 0)
        {
            throw new PlaylistException(
                422,
                "invalid_playlist_operation",
                "after_revision must be a non-negative integer."
            );
        }
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken
        );
        var playlist = await AuthorizedPlaylistAsync(actor, id, cancellationToken);
        var generation = await GenerationAsync(cancellationToken);
        if (afterRevision == 0 || afterRevision > playlist.Revision)
        {
            throw SnapshotRequired();
        }

        if (afterRevision == playlist.Revision)
        {
            await transaction.CommitAsync(cancellationToken);
            return new(1, generation, id, playlist.Revision, [], afterRevision, false);
        }

        // Decide the page from lightweight headers first. Fetching 501 full envelopes could
        // read hundreds of large rebalances even when only the first belongs in this page.
        var headers = await dbContext
            .PlaylistRevisions.AsNoTracking()
            .Where(row => row.PlaylistId == id && row.Revision > afterRevision)
            .OrderBy(row => row.Revision)
            .Select(row => new { row.Revision, row.ChangeCount })
            .Take(501)
            .ToArrayAsync(cancellationToken);
        var next = afterRevision;
        var count = 0;
        foreach (var row in headers)
        {
            if (row.Revision != next + 1)
            {
                throw SnapshotRequired();
            }
            if (next > afterRevision && count + row.ChangeCount > 500)
            {
                break;
            }
            next = row.Revision;
            count += row.ChangeCount;
        }
        if (next == afterRevision)
        {
            throw SnapshotRequired();
        }
        var rows = await dbContext
            .PlaylistRevisions.AsNoTracking()
            .Where(row =>
                row.PlaylistId == id && row.Revision > afterRevision && row.Revision <= next
            )
            .OrderBy(row => row.Revision)
            .ToArrayAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var page = rows.Select(row => new PlaylistRevision(
                row.Revision,
                row.Kind,
                JsonSerializer.Deserialize<List<PlaylistChange>>(
                    row.ChangesJson,
                    PlaylistJson.Options
                )!
            ))
            .ToArray();
        return new(1, generation, id, playlist.Revision, page, next, next < playlist.Revision);
    }

    private async Task<Playlist> AuthorizedPlaylistAsync(
        UserId actor,
        PlaylistId id,
        CancellationToken cancellationToken
    ) =>
        await dbContext
            .Playlists.AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == id && row.OwnerId == actor, cancellationToken)
        ?? throw new PlaylistException(404, "playlist_not_found", "Playlist not found.");

    private Task<string> GenerationAsync(CancellationToken cancellationToken) =>
        dbContext.HistoryGenerations.Select(row => row.Value).SingleAsync(cancellationToken);

    private static PlaylistException SnapshotRequired() =>
        new(410, "snapshot_required", "Fetch a fresh playlist snapshot.");
}
