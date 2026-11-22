using Relisten.Accounts.Contracts.Playlists;
using RelistenUserService.Playlists.Entities;

namespace RelistenUserService.Playlists;

/// <summary>
/// Evaluates a batch once against a detached working copy. Nothing here writes to EF or the
/// database. The caller checks final quotas and persists this exact state and its envelopes.
/// </summary>
internal sealed class PlaylistEditor(
    Playlist playlist,
    IEnumerable<PlaylistSegment> segments,
    IEnumerable<PlaylistEntry> entries,
    HashSet<string> existingCreatedIds
)
{
    public Playlist Playlist { get; } = playlist;
    public Dictionary<PlaylistSegmentId, PlaylistSegment> Segments { get; } =
        segments.ToDictionary(row => row.Id);
    public Dictionary<PlaylistEntryId, PlaylistEntry> Entries { get; } =
        entries.ToDictionary(row => row.Id);
    public List<PlaylistRevision> Revisions { get; } = [];
    public int LiveEntryCount => Playlist.EntryCount;

    public void CheckGuideQuota()
    {
        var segments = Segments.Values.Where(row => !row.Deleted).ToArray();
        var entries = Entries.Values.Where(row => !row.Deleted).ToArray();
        var occupiedSegments = entries.Select(row => row.SegmentId).ToHashSet();
        PlaylistValidation.CheckGuideQuota(
            segments.Count(row => !occupiedSegments.Contains(row.Id)),
            segments
                .Select(row => row.Description)
                .Concat(entries.Select(row => row.Description))
                .Append(Playlist.Description)
        );
    }

    public async Task<PlaylistOperationResult> ApplyAsync(
        PlaylistOperation operation,
        Func<IReadOnlyList<PlaylistEntryInput>, Task<bool>> entriesArePlayable
    )
    {
        var changes = new List<PlaylistChange>();
        var status = operation.Payload switch
        {
            UpdatePlaylistMetadata metadata => UpdateMetadata(metadata, changes),
            InsertPlaylistSegment insert => await InsertSegmentAsync(
                insert,
                entriesArePlayable,
                changes
            ),
            UpdatePlaylistSegment update => UpdateSegment(update, changes),
            MovePlaylistSegment move => MoveSegment(move, changes),
            DeletePlaylistSegment delete => DeleteSegment(delete, changes),
            InsertPlaylistEntries insert => await InsertEntriesAsync(
                insert,
                entriesArePlayable,
                changes
            ),
            UpdatePlaylistEntry update => UpdateEntry(update, changes),
            MovePlaylistEntry move => MoveEntry(move, changes),
            DeletePlaylistEntry delete => DeleteEntry(delete, changes),
            _ => throw new InvalidOperationException("Unknown operation payload."),
        };

        if (changes.Count == 0)
        {
            return new(operation.OperationId, status, null);
        }
        Publish("content", changes);
        return new(operation.OperationId, "applied", Playlist.Revision);
    }

    private string UpdateMetadata(UpdatePlaylistMetadata patch, List<PlaylistChange> changes)
    {
        var name = patch.Name.IsPresent ? patch.Name.Value! : Playlist.Name;
        var description = patch.Description.IsPresent
            ? patch.Description.Value
            : Playlist.Description;
        if (name == Playlist.Name && description == Playlist.Description)
        {
            return "no_op";
        }
        Playlist.Name = name;
        Playlist.Description = description;
        changes.Add(new PlaylistMetadataUpdated(name, description));
        return "applied";
    }

    private async Task<string> InsertSegmentAsync(
        InsertPlaylistSegment insert,
        Func<IReadOnlyList<PlaylistEntryInput>, Task<bool>> entriesArePlayable,
        List<PlaylistChange> changes
    )
    {
        if (
            Conflicts([
                insert.SegmentId.Value,
                .. insert.Entries.Select(entry => entry.EntryId.Value),
            ])
        )
        {
            return "id_conflict";
        }
        if (!await entriesArePlayable(insert.Entries))
        {
            return "track_unavailable";
        }

        var siblings = LiveSegments();
        var index = Position(
            siblings.Select(row => row.Id).ToList(),
            insert.AfterSegmentId,
            insert.BeforeSegmentId
        );
        var rank = SegmentRanks(siblings, index, 1)[0];
        AddSegment(insert.SegmentId, rank, insert.Description, changes);
        AddEntries(
            insert.SegmentId,
            insert.Entries,
            PlaylistRanks.Between(null, null, insert.Entries.Count),
            changes
        );
        return "applied";
    }

    private string UpdateSegment(UpdatePlaylistSegment update, List<PlaylistChange> changes)
    {
        var status = SegmentStatus(update.SegmentId);
        if (status is not null)
        {
            return status;
        }

        var segment = Segments[update.SegmentId];
        if (!update.Description.IsPresent || segment.Description == update.Description.Value)
        {
            return "no_op";
        }

        segment.Description = update.Description.Value;
        if (!DeleteEmptySegment(segment.Id, changes))
        {
            changes.Add(Upsert(segment));
        }

        return "applied";
    }

    private string MoveSegment(MovePlaylistSegment move, List<PlaylistChange> changes)
    {
        var targetStatus = SegmentStatus(move.SegmentId);
        if (targetStatus is not null)
        {
            return targetStatus;
        }
        var target = Segments[move.SegmentId];
        // Repeated append intents should not allocate and sort every sibling to prove a no-op.
        if (
            move.AfterSegmentId is null
            && move.BeforeSegmentId is null
            && !Segments.Values.Any(row =>
                !row.Deleted && string.CompareOrdinal(row.Rank, target.Rank) > 0
            )
        )
        {
            return "no_op";
        }

        var ordered = LiveSegments();
        var previousIndex = ordered.FindIndex(row => row.Id == target.Id);
        ordered.RemoveAt(previousIndex);
        var index = Position(
            ordered.Select(row => row.Id).ToList(),
            move.AfterSegmentId,
            move.BeforeSegmentId
        );
        if (index == previousIndex)
        {
            return "no_op";
        }
        target.Rank = SegmentRanks(ordered, index, 1)[0];
        changes.Add(Upsert(target));
        return "applied";
    }

    private string DeleteSegment(DeletePlaylistSegment delete, List<PlaylistChange> changes)
    {
        var status = SegmentStatus(delete.SegmentId);
        if (status is not null)
        {
            return status;
        }
        foreach (var entry in LiveEntries(delete.SegmentId))
        {
            TombstoneEntry(entry);
        }
        TombstoneSegment(delete.SegmentId, changes);
        return "applied";
    }

    private string UpdateEntry(UpdatePlaylistEntry update, List<PlaylistChange> changes)
    {
        var status = EntryStatus(update.EntryId);
        if (status is not null)
        {
            return status;
        }

        var entry = Entries[update.EntryId];
        if (!update.Description.IsPresent || entry.Description == update.Description.Value)
        {
            return "no_op";
        }
        if (entry.TrackId is null && update.Description.Value is null)
        {
            throw PlaylistValidation.Invalid("Delete a text entry to remove its description.");
        }

        entry.Description = update.Description.Value;
        changes.Add(Upsert(entry));
        return "applied";
    }

    private async Task<string> InsertEntriesAsync(
        InsertPlaylistEntries insert,
        Func<IReadOnlyList<PlaylistEntryInput>, Task<bool>> entriesArePlayable,
        List<PlaylistChange> changes
    )
    {
        if (SegmentStatus(insert.SegmentId) is not null)
        {
            return "parent_missing";
        }
        if (Conflicts(insert.Entries.Select(entry => entry.EntryId.Value)))
        {
            return "id_conflict";
        }
        if (!await entriesArePlayable(insert.Entries))
        {
            return "track_unavailable";
        }

        var siblings = LiveEntries(insert.SegmentId);
        var index = Position(
            siblings.Select(row => row.Id).ToList(),
            insert.AfterEntryId,
            insert.BeforeEntryId
        );
        AddEntries(
            insert.SegmentId,
            insert.Entries,
            EntryRanks(insert.SegmentId, siblings, index, insert.Entries.Count),
            changes
        );
        return "applied";
    }

    private string MoveEntry(MovePlaylistEntry move, List<PlaylistChange> changes)
    {
        var status = EntryStatus(move.EntryId);
        if (status is not null)
        {
            return status;
        }
        var entry = Entries[move.EntryId];
        var oldParent = entry.SegmentId;
        var destination = move.Destination;
        string rank;
        PlaylistSegmentId newParent;
        if (destination.NewSegmentId is { } newId)
        {
            if (Conflicts([newId.Value]))
            {
                return "id_conflict";
            }
            // Resolve against the old segment before tombstoning it: pulling out its last
            // entry may still name that segment as a placement anchor for the new segment.
            var segments = LiveSegments();
            var index = Position(
                segments.Select(row => row.Id).ToList(),
                destination.AfterSegmentId,
                destination.BeforeSegmentId
            );
            var segmentRank = SegmentRanks(segments, index, 1)[0];
            AddSegment(newId, segmentRank, null, changes);
            newParent = newId;
            rank = "a0";
        }
        else
        {
            newParent = destination.SegmentId!.Value;
            if (SegmentStatus(newParent) is not null)
            {
                return "parent_missing";
            }
            // Keep this check before building ordered siblings for large offline batches.
            if (
                newParent == oldParent
                && destination.AfterEntryId is null
                && destination.BeforeEntryId is null
                && !Entries.Values.Any(row =>
                    !row.Deleted
                    && row.SegmentId == newParent
                    && string.CompareOrdinal(row.Rank, entry.Rank) > 0
                )
            )
            {
                return "no_op";
            }

            var siblings = LiveEntries(newParent);
            var previousIndex = siblings.FindIndex(row => row.Id == entry.Id);
            siblings.RemoveAll(row => row.Id == entry.Id);
            var index = Position(
                siblings.Select(row => row.Id).ToList(),
                destination.AfterEntryId,
                destination.BeforeEntryId
            );
            if (newParent == oldParent && index == previousIndex)
            {
                return "no_op";
            }
            rank = EntryRanks(newParent, siblings, index, 1)[0];
        }

        entry.SegmentId = newParent;
        entry.Rank = rank;
        changes.Add(Upsert(entry));
        DeleteEmptySegment(oldParent, changes);
        return "applied";
    }

    private string DeleteEntry(DeletePlaylistEntry delete, List<PlaylistChange> changes)
    {
        var status = EntryStatus(delete.EntryId);
        if (status is not null)
        {
            return status;
        }
        var entry = Entries[delete.EntryId];
        TombstoneEntry(entry);
        changes.Add(new PlaylistEntryDeleted(entry.Id));
        DeleteEmptySegment(entry.SegmentId, changes);
        return "applied";
    }

    private string? SegmentStatus(PlaylistSegmentId id) =>
        !Segments.TryGetValue(id, out var segment) ? "target_missing"
        : segment.Deleted ? "target_deleted"
        : null;

    private string? EntryStatus(PlaylistEntryId id) =>
        !Entries.TryGetValue(id, out var entry) ? "target_missing"
        : entry.Deleted ? "target_deleted"
        : null;

    private bool Conflicts(IEnumerable<string> ids) => ids.Any(existingCreatedIds.Contains);

    private List<PlaylistSegment> LiveSegments() =>
        Segments
            .Values.Where(row => !row.Deleted)
            .OrderBy(row => row.Rank, StringComparer.Ordinal)
            .ToList();

    private List<PlaylistEntry> LiveEntries(PlaylistSegmentId parent) =>
        Entries
            .Values.Where(row => !row.Deleted && row.SegmentId == parent)
            .OrderBy(row => row.Rank, StringComparer.Ordinal)
            .ToList();

    private void AddSegment(
        PlaylistSegmentId id,
        string rank,
        string? description,
        List<PlaylistChange> changes
    )
    {
        var segment = new PlaylistSegment
        {
            Id = id,
            PlaylistId = Playlist.Id,
            Description = description,
            Rank = rank,
        };
        Segments.Add(id, segment);
        existingCreatedIds.Add(id.Value);
        changes.Add(Upsert(segment));
    }

    private void AddEntries(
        PlaylistSegmentId parent,
        IReadOnlyList<PlaylistEntryInput> entries,
        string[] ranks,
        List<PlaylistChange> changes
    )
    {
        for (var index = 0; index < entries.Count; index++)
        {
            var input = entries[index];
            var entry = new PlaylistEntry
            {
                Id = input.EntryId,
                PlaylistId = Playlist.Id,
                SegmentId = parent,
                TrackId = input.TrackId,
                Description = input.Description,
                Rank = ranks[index],
            };
            Entries.Add(entry.Id, entry);
            existingCreatedIds.Add(input.EntryId.Value);
            changes.Add(Upsert(entry));
        }
        Playlist.EntryCount += entries.Count;
        Playlist.TrackCount += entries.Count(entry => entry.TrackId is not null);
    }

    private void TombstoneEntry(PlaylistEntry entry)
    {
        entry.Deleted = true;
        entry.Rank = null;
        Playlist.EntryCount--;
        if (entry.TrackId is not null)
        {
            Playlist.TrackCount--;
        }
    }

    private bool DeleteEmptySegment(PlaylistSegmentId id, List<PlaylistChange> changes)
    {
        if (
            Segments[id].Description is not null
            || Entries.Values.Any(row => row.SegmentId == id && !row.Deleted)
        )
        {
            return false;
        }

        TombstoneSegment(id, changes);
        return true;
    }

    private void TombstoneSegment(PlaylistSegmentId id, List<PlaylistChange> changes)
    {
        Segments[id].Deleted = true;
        Segments[id].Rank = null;
        changes.Add(new PlaylistSegmentDeleted(id));
    }

    private string[] SegmentRanks(List<PlaylistSegment> siblings, int index, int count)
    {
        var ranks = Allocate(siblings.Select(row => row.Rank!).ToList(), index, count);
        if (ranks.Any(rank => rank.Length > PlaylistRanks.MaximumLength))
        {
            var live = LiveSegments();
            var balanced = PlaylistRanks.Between(null, null, live.Count);
            var changes = new List<PlaylistChange>();
            for (var i = 0; i < live.Count; i++)
            {
                live[i].Rank = balanced[i];
                changes.Add(Upsert(live[i]));
            }
            Publish("rebalance", changes);
            ranks = Allocate(siblings.Select(row => row.Rank!).ToList(), index, count);
        }
        return ranks;
    }

    private string[] EntryRanks(
        PlaylistSegmentId parent,
        List<PlaylistEntry> siblings,
        int index,
        int count
    )
    {
        var ranks = Allocate(siblings.Select(row => row.Rank!).ToList(), index, count);
        if (ranks.Any(rank => rank.Length > PlaylistRanks.MaximumLength))
        {
            var live = LiveEntries(parent);
            var balanced = PlaylistRanks.Between(null, null, live.Count);
            var changes = new List<PlaylistChange>();
            for (var i = 0; i < live.Count; i++)
            {
                live[i].Rank = balanced[i];
                changes.Add(Upsert(live[i]));
            }
            Publish("rebalance", changes);
            ranks = Allocate(siblings.Select(row => row.Rank!).ToList(), index, count);
        }
        return ranks;
    }

    private void Publish(string kind, List<PlaylistChange> changes) =>
        Revisions.Add(new(++Playlist.Revision, kind, changes));

    private static PlaylistSegmentUpserted Upsert(PlaylistSegment segment) =>
        new(segment.Id, segment.Rank!, segment.Description);

    private static PlaylistEntryUpserted Upsert(PlaylistEntry entry) =>
        new(entry.Id, entry.SegmentId, entry.TrackId, entry.Rank!, entry.Description);

    private static string[] Allocate(List<string> ranks, int index, int count) =>
        PlaylistRanks.Between(
            index == 0 ? null : ranks[index - 1],
            index == ranks.Count ? null : ranks[index],
            count
        );

    private static int Position<T>(List<T> children, T? after, T? before)
        where T : struct
    {
        var afterIndex = after is { } a ? children.IndexOf(a) : -1;
        if (afterIndex >= 0)
        {
            return afterIndex + 1;
        }
        var beforeIndex = before is { } b ? children.IndexOf(b) : -1;
        return beforeIndex >= 0 ? beforeIndex : children.Count;
    }
}
