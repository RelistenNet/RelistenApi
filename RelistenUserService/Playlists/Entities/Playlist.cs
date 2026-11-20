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

    // Duration snapshots can be incomplete. Keep the subtotal so a later removal or backfill
    // can complete it without asking Catalog for the tracks we already know.
    public long KnownDurationSeconds { get; set; }
    public int TracksWithDuration { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    internal long? DurationSeconds =>
        TrackCount == TracksWithDuration ? KnownDurationSeconds : null;

    internal void AddTrack(int? duration)
    {
        TrackCount++;
        if (duration is { } seconds)
        {
            KnownDurationSeconds += seconds;
            TracksWithDuration++;
        }
    }

    internal void RemoveTrack(int? duration)
    {
        TrackCount--;
        if (duration is { } seconds)
        {
            KnownDurationSeconds -= seconds;
            TracksWithDuration--;
        }
    }
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
    public int? TrackDurationSeconds { get; set; }
    public string? Description { get; set; }
    public string? Rank { get; set; }
    public bool Deleted { get; set; }
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
