namespace RelistenUserService.Playlists.Entities;

public sealed class PlaylistFollow
{
    public UserId UserId { get; set; }
    public PlaylistId PlaylistId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class PlaylistReport
{
    public PlaylistReportId Id { get; set; }
    public UserId UserId { get; set; }
    public PlaylistId PlaylistId { get; set; }
    public string Reason { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
}
