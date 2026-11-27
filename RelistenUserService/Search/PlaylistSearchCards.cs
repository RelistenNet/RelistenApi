using Microsoft.EntityFrameworkCore;
using Relisten.Search;
using RelistenUserService.Persistence;
using RelistenUserService.Playlists;
using RelistenUserService.Playlists.Entities;

namespace RelistenUserService.Search;

internal sealed class PlaylistSearchCards(AccountsDbContext db)
{
    internal async Task<PlaylistSearchCard[]> ExactLibraryTitle(
        MergedSearchRequest request,
        UserId? viewer,
        CancellationToken cancellationToken
    )
    {
        var title = request.Q?.Trim().Normalize().ToLowerInvariant();
        if (
            viewer is not { } user
            || !request.IsAll
            || request.Page != 1
            || request.ArtistId != null
            || string.IsNullOrEmpty(title)
            || title == "*"
        )
        {
            return [];
        }

        // Read current names so a freshly saved or renamed playlist is searchable immediately.
        var libraryIds = db
            .Playlists.Where(row => row.OwnerId == user)
            .Select(row => row.Id)
            .Union(
                db.PlaylistFollows.Where(follow => follow.UserId == user)
                    .Select(follow => follow.PlaylistId)
            );
        var library = db
            .Playlists.AsNoTracking()
            .VisibleTo(user)
            .Where(row => libraryIds.Contains(row.Id) && row.Name.Trim().ToLower() == title)
            .OrderByDescending(row => row.OwnerId == user)
            .ThenByDescending(row => row.UpdatedAt)
            .ThenBy(row => row.Id)
            .Take(PlaylistSearchCandidates.AllPageSize);
        var rows = await CurrentRows(library).ToArrayAsync(cancellationToken);
        var popularity = await ReadPopularity(rows, cancellationToken);
        return rows.Select(row =>
                row.Card(
                    user,
                    followed: row.OwnerId != user,
                    popularity.GetValueOrDefault(row.Id.Value)
                )
            )
            .ToArray();
    }

    public async Task<PlaylistSearchCard[]> Read(
        PlaylistCandidate[] candidates,
        UserId? viewer,
        CancellationToken cancellationToken
    )
    {
        var ids = candidates
            .Select(candidate =>
                PlaylistId.TryParse(candidate.Id, out var id) ? id : (PlaylistId?)null
            )
            .OfType<PlaylistId>()
            .Distinct()
            .ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        var visible = db
            .Playlists.AsNoTracking()
            .VisibleTo(viewer)
            .Where(row => ids.Contains(row.Id));
        var rows = await CurrentRows(visible)
            .ToDictionaryAsync(row => row.Id.Value, cancellationToken);

        var popularity = await ReadPopularity(rows.Values, cancellationToken);
        var follows = viewer is { } user
            ? await db
                .PlaylistFollows.Where(follow =>
                    follow.UserId == user && ids.Contains(follow.PlaylistId)
                )
                .Select(follow => follow.PlaylistId)
                .ToHashSetAsync(cancellationToken)
            : [];
        return candidates
            .Where(candidate =>
                rows.TryGetValue(candidate.Id, out var row) && row.Matches(candidate)
            )
            .Select(candidate =>
                rows[candidate.Id]
                    .Card(
                        viewer,
                        follows.Contains(rows[candidate.Id].Id),
                        popularity.GetValueOrDefault(candidate.Id),
                        candidate.PopularityPercentile,
                        candidate.SupportingMatch
                    )
            )
            .ToArray();
    }

    private Task<Dictionary<string, PlaylistPopularity>> ReadPopularity(
        IEnumerable<CurrentPlaylist> rows,
        CancellationToken ct
    ) =>
        PlaylistPopularity.ReadAsync(db, rows.Where(row => row.IsPublic).Select(row => row.Id), ct);

    private IQueryable<CurrentPlaylist> CurrentRows(IQueryable<Playlist> playlists) =>
        from row in playlists
        from generation in db.HistoryGenerations
        select new CurrentPlaylist(
            row.Id,
            row.OwnerId,
            row.Name,
            row.Description,
            row.TrackCount,
            row.FollowerCount,
            row.Published,
            row.Archived,
            row.TakenDown,
            row.Revision,
            row.PublicCode,
            generation.Value,
            row.Owner.Username
        );

    private sealed record CurrentPlaylist(
        PlaylistId Id,
        UserId OwnerId,
        string Name,
        string? Description,
        int TrackCount,
        int FollowerCount,
        bool Published,
        bool Archived,
        bool TakenDown,
        long Revision,
        string? PublicCode,
        string HistoryGeneration,
        string Username
    )
    {
        public bool IsPublic => Published && !Archived && !TakenDown;

        public bool Matches(PlaylistCandidate hit) =>
            hit.HistoryGeneration == HistoryGeneration
            && hit.Revision == Revision
            && hit.Published == Published
            && hit.Archived == Archived
            && hit.TakenDown == TakenDown;

        // The old handle can match until reindexing, while the card shows the current username.
        public PlaylistSearchCard Card(
            UserId? viewer,
            bool followed,
            PlaylistPopularity? popularity,
            double popularityPercentile = 0,
            MatchEvidence? supportingMatch = null
        ) =>
            new(
                Id,
                Name,
                Description is { Length: > 240 } ? Description[..240] : Description,
                TrackCount,
                viewer == OwnerId,
                IsPublic ? PublicCode : null,
                IsPublic ? Username : null,
                followed,
                IsPublic ? FollowerCount : null,
                popularity,
                popularityPercentile,
                supportingMatch
            );
    }
}

internal sealed record PlaylistSearchCard(
    PlaylistId Id,
    string Label,
    string? Description,
    int TrackCount,
    bool Owned,
    string? PublicCode,
    string? Curator,
    bool Followed,
    int? FollowerCount,
    PlaylistPopularity? Popularity,
    double PopularityPercentile,
    MatchEvidence? SupportingMatch
)
{
    public string Kind => "playlist";
}
