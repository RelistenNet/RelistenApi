using Microsoft.EntityFrameworkCore;
using RelistenUserService.Persistence;
using RelistenUserService.Playlists;

namespace RelistenUserService.Search;

internal sealed class PlaylistSearchCards(AccountsDbContext db)
{
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

        var visible = db.Playlists.AsNoTracking().VisibleTo(viewer);
        var rows = await (
            from row in visible
            from generation in db.HistoryGenerations
            where ids.Contains(row.Id)
            select new CurrentPlaylist(
                row.Id,
                row.OwnerId,
                row.Name,
                row.Description,
                row.TrackCount,
                row.Published,
                row.Archived,
                row.TakenDown,
                row.Revision,
                row.PublicCode,
                generation.Value,
                row.Owner.Username
            )
        ).ToDictionaryAsync(row => row.Id.Value, cancellationToken);

        return candidates
            .Where(candidate =>
                rows.TryGetValue(candidate.Id, out var row) && row.Matches(candidate)
            )
            .Select(candidate => rows[candidate.Id].Card(viewer))
            .ToArray();
    }

    private sealed record CurrentPlaylist(
        PlaylistId Id,
        UserId OwnerId,
        string Name,
        string? Description,
        int TrackCount,
        bool Published,
        bool Archived,
        bool TakenDown,
        long Revision,
        string? PublicCode,
        string HistoryGeneration,
        string Username
    )
    {
        private bool IsPublic => Published && !Archived && !TakenDown;

        public bool Matches(PlaylistCandidate hit) =>
            hit.HistoryGeneration == HistoryGeneration
            && hit.Revision == Revision
            && hit.Published == Published
            && hit.Archived == Archived
            && hit.TakenDown == TakenDown;

        // The old handle can match until reindexing, while the card shows the current username.
        public PlaylistSearchCard Card(UserId? viewer) =>
            new(
                Id,
                Name,
                Description is { Length: > 240 } ? Description[..240] : Description,
                TrackCount,
                viewer == OwnerId,
                IsPublic ? PublicCode : null,
                IsPublic ? Username : null
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
    string? Curator
)
{
    public string Kind => "playlist";
}
