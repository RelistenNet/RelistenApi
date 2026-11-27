using RelistenUserService.Identity.Entities;

namespace RelistenUserService.Playlists.Entities;

public sealed class Playlist
{
    public PlaylistId Id { get; set; }
    public UserId OwnerId { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public bool Archived { get; set; }
    public string? PublicCode { get; set; }
    public bool Published { get; set; }
    public bool TakenDown { get; set; }
    public User Owner { get; set; } = null!;
    public long Revision { get; set; }
    public int EntryCount { get; set; }

    // Live entries with a track, maintained beside EntryCount so summaries need no entry scan.
    public int TrackCount { get; set; }
    public int FollowerCount { get; set; }

    // Catalog facts derived from live track entries by PlaylistCatalogIndex, beside the
    // playlist_artists rows. They are current when CatalogIndexedRevision equals Revision.
    public int CatalogDurationSeconds { get; set; }
    public long CatalogIndexedRevision { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class PlaylistSegment
{
    public PlaylistSegmentId Id { get; set; }
    public PlaylistId PlaylistId { get; set; }
    public string? Description { get; set; }
    public string? Rank { get; set; }
    public bool Deleted { get; set; }
}

public sealed class PlaylistEntry
{
    public PlaylistEntryId Id { get; set; }
    public PlaylistId PlaylistId { get; set; }
    public PlaylistSegmentId SegmentId { get; set; }
    public string? TrackId { get; set; }
    public string? Description { get; set; }
    public string? Rank { get; set; }
    public bool Deleted { get; set; }
}

/// <summary>
/// One catalog artist with at least one live track in a playlist. Rebuilt with the
/// playlist's other catalog facts; never edited directly.
/// </summary>
public sealed class PlaylistArtist
{
    public PlaylistId PlaylistId { get; set; }
    public Guid ArtistUuid { get; set; }
    public int TrackCount { get; set; }
    public int FollowerCount { get; set; }
}

public sealed class PlaylistRevisionRow
{
    public PlaylistId PlaylistId { get; set; }
    public long Revision { get; set; }
    public string Kind { get; set; } = "content";
    public int ChangeCount { get; set; }
    public string ChangesJson { get; set; } = "[]";
}

// Complete typed receipt IDs are globally unique across playlist commands and operations.
// Stored outcomes are independent of response envelopes (current head/history generation).
public sealed class PlaylistReceipt
{
    public string Id { get; set; } = "";
    public UserId ActorId { get; set; }
    public PlaylistId PlaylistId { get; set; }
    public string Kind { get; set; } = "";
    public byte[] PayloadHash { get; set; } = [];
    public string? Status { get; set; }
    public long? Revision { get; set; }
    public bool? Archived { get; set; }
    public bool Changed { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class HistoryGeneration
{
    public int Id { get; set; } = 1;
    public string Value { get; set; } = "";
}
