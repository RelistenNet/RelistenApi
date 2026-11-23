using System.Text.Json.Serialization;
using Relisten.Accounts.Contracts.Identifiers;

namespace Relisten.Accounts.Contracts.Playlists;

public sealed record PlaylistSummary(
    PlaylistId PlaylistId,
    string Name,
    int EntryCount,
    bool Archived,
    long Revision,
    DateTimeOffset UpdatedAt
)
{
    // Library changes stored before guide content have only track entries.
    public int TrackCount { get; init; } = EntryCount;

    /// <summary>The author's Markdown, or null when there is none.</summary>
    public string? Description { get; init; }
    public string? PublicCode { get; init; }
    public bool Published { get; init; }
    public bool TakenDown { get; init; }
}

public sealed record PlaylistList(
    int ContractVersion,
    string HistoryGeneration,
    IReadOnlyList<PlaylistSummary> Playlists
);

public sealed record PlaylistCreated(
    int ContractVersion,
    string HistoryGeneration,
    PlaylistId PlaylistId,
    long Revision
);

public sealed record PlaylistArchived(
    int ContractVersion,
    string HistoryGeneration,
    PlaylistId PlaylistId,
    bool Archived,
    bool Changed
);

public sealed record PlaylistEntrySnapshot(
    PlaylistEntryId EntryId,
    string? TrackId,
    string Rank,
    string? Description = null
);

public sealed record PlaylistSegmentSnapshot(
    PlaylistSegmentId SegmentId,
    string Rank,
    IReadOnlyList<PlaylistEntrySnapshot> Entries,
    string? Description = null
);

public sealed record PlaylistSnapshot(
    int ContractVersion,
    string HistoryGeneration,
    PlaylistId PlaylistId,
    long Revision,
    bool Archived,
    string Role,
    PlaylistMetadata Metadata,
    IReadOnlyList<PlaylistSegmentSnapshot> Segments,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
)
{
    public string? PublicCode { get; init; }
    public bool Published { get; init; }
    public bool TakenDown { get; init; }
}

public sealed record PlaylistOperationResult(
    PlaylistOperationId OperationId,
    string Status,
    long? Revision
);

public sealed record PlaylistBatchResponse(
    int ContractVersion,
    string HistoryGeneration,
    long HeadRevision,
    IReadOnlyList<PlaylistOperationResult> Results
);

public sealed record PlaylistRevision(
    long Revision,
    string Kind,
    IReadOnlyList<PlaylistChange> Changes
);

public sealed record PlaylistChanges(
    int ContractVersion,
    string HistoryGeneration,
    PlaylistId PlaylistId,
    long HeadRevision,
    IReadOnlyList<PlaylistRevision> Revisions,
    long NextAfterRevision,
    bool HasMore
);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PlaylistMetadataUpdated), "metadata_updated")]
[JsonDerivedType(typeof(PlaylistSegmentUpserted), "segment_upserted")]
[JsonDerivedType(typeof(PlaylistSegmentDeleted), "segment_deleted")]
[JsonDerivedType(typeof(PlaylistEntryUpserted), "entry_upserted")]
[JsonDerivedType(typeof(PlaylistEntryDeleted), "entry_deleted")]
public abstract record PlaylistChange;

public sealed record PlaylistMetadataUpdated(string Name, string? Description) : PlaylistChange;

public sealed record PlaylistSegmentUpserted(
    PlaylistSegmentId SegmentId,
    string Rank,
    string? Description = null
) : PlaylistChange;

public sealed record PlaylistSegmentDeleted(PlaylistSegmentId SegmentId) : PlaylistChange;

public sealed record PlaylistEntryUpserted(
    PlaylistEntryId EntryId,
    PlaylistSegmentId SegmentId,
    string? TrackId,
    string Rank,
    string? Description = null
) : PlaylistChange;

public sealed record PlaylistEntryDeleted(PlaylistEntryId EntryId) : PlaylistChange;
