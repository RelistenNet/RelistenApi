using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Relisten.Accounts.Contracts.Playlists;
using RelistenUserService.Configuration;
using RelistenUserService.Library;
using RelistenUserService.Library.Entities;
using RelistenUserService.Persistence;
using RelistenUserService.Playlists.Entities;

namespace RelistenUserService.Playlists;

/// <summary>
/// Owns every private-playlist transaction. Identity locks precede the owner's library-state
/// lock, then the playlist lock. A future account-deletion fence attaches before library state.
/// </summary>
public sealed class PlaylistWriteService(
    AccountsDbContext dbContext,
    AdvisoryLockService advisoryLocks,
    LibraryStateStore stateStore,
    TimeProvider timeProvider,
    AccountsOptions options
)
{
    public async Task<(PlaylistCreated Response, bool Replayed)> CreateAsync(
        UserId actor,
        CreatePlaylistRequest request,
        CancellationToken cancellationToken
    )
    {
        request = PlaylistValidation.Normalize(request);
        var createdIds = new[] { request.PlaylistId.Value }
            .Concat(
                request.Segments.SelectMany(segment =>
                    new[] { segment.SegmentId.Value }.Concat(
                        segment.Entries.Select(entry => entry.EntryId.Value)
                    )
                )
            )
            .ToArray();
        var hash = PlaylistHasher.Hash(request);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            cancellationToken
        );
        var state = await LockOwnerAsync(
            actor,
            [request.CommandId.Value],
            createdIds,
            cancellationToken
        );
        var generation = await GenerationAsync(cancellationToken);
        var receipt = await dbContext.PlaylistReceipts.FindAsync(
            [request.CommandId.Value],
            cancellationToken
        );
        if (receipt is not null)
        {
            CheckReceipt(receipt, actor, request.PlaylistId, hash);
            await transaction.CommitAsync(cancellationToken);
            return (new(1, generation, request.PlaylistId, receipt.Revision!.Value), true);
        }

        if (
            !options.EnablePlaylistGuideContent
            && request.Segments.Any(PlaylistValidation.UsesGuideContent)
        )
        {
            throw GuideContentDisabled();
        }

        var conflicts = await ExistingIdsAsync(createdIds, cancellationToken);
        if (conflicts.Count > 0)
        {
            throw new PlaylistException(
                409,
                "id_conflict",
                "A created ID already exists.",
                conflicts.Order(StringComparer.Ordinal).ToArray()
            );
        }
        var ownerPlaylists = await dbContext
            .Playlists.Where(row => row.OwnerId == actor)
            .ToArrayAsync(cancellationToken);
        var count = request.Segments.Sum(segment => segment.Entries.Count);
        CheckQuota(
            ownerPlaylists.Length + 1,
            count,
            ownerPlaylists.Sum(row => row.EntryCount) + count
        );
        var entries = request.Segments.SelectMany(segment => segment.Entries);
        PlaylistValidation.CheckGuideQuota(
            request.Segments.Count(segment => segment.Entries.Count == 0),
            request
                .Segments.Select(segment => segment.Description)
                .Concat(entries.Select(entry => entry.Description))
                .Append(request.Metadata.Description)
        );

        var now = timeProvider.GetUtcNow();
        var playlist = new Playlist
        {
            Id = request.PlaylistId,
            OwnerId = actor,
            Name = request.Metadata.Name,
            Description = request.Metadata.Description,
            Revision = 1,
            EntryCount = count,
            CreatedAt = now,
            UpdatedAt = now,
        };
        dbContext.Playlists.Add(playlist);
        AddInitialStructure(request);
        dbContext.PlaylistReceipts.Add(
            new()
            {
                Id = request.CommandId.Value,
                ActorId = actor,
                PlaylistId = playlist.Id,
                Kind = "create",
                PayloadHash = hash,
                Revision = 1,
                Changed = true,
                CreatedAt = now,
            }
        );
        AppendLibrarySummary(
            state,
            playlist,
            entries.Count(entry => entry.TrackId is not null),
            now
        );
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (new(1, generation, playlist.Id, 1), false);
    }

    public async Task<PlaylistBatchResponse> ApplyAsync(
        UserId actor,
        PlaylistId id,
        PlaylistBatchRequest request,
        CancellationToken cancellationToken
    )
    {
        request = PlaylistValidation.Normalize(request);
        var hashes = request.Operations.ToDictionary(
            operation => operation.OperationId.Value,
            operation => PlaylistHasher.Hash(id, request.ContractVersion, operation)
        );
        var createdIds = request.Operations.SelectMany(PlaylistValidation.CreatedIds).ToArray();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            cancellationToken
        );
        var state = await LockOwnerAsync(actor, hashes.Keys, createdIds, cancellationToken);
        var playlist = await LockOwnedPlaylistAsync(actor, id, cancellationToken);
        var generation = await GenerationAsync(cancellationToken);

        var receiptIds = hashes.Keys.ToArray();
        var receipts = await dbContext
            .PlaylistReceipts.Where(row => receiptIds.Contains(row.Id))
            .ToDictionaryAsync(row => row.Id, cancellationToken);
        var conflicts = receipts
            .Values.Where(receipt => !Matches(receipt, actor, id, hashes[receipt.Id]))
            .Select(receipt => receipt.Id)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (conflicts.Length > 0)
        {
            throw new PlaylistException(
                409,
                "idempotency_conflict",
                "An operation ID was reused with another actor, playlist or payload.",
                conflicts
            );
        }
        if (receipts.Count == request.Operations.Count)
        {
            await transaction.CommitAsync(cancellationToken);
            return new(
                1,
                generation,
                playlist.Revision,
                request
                    .Operations.Select(operation => Result(receipts[operation.OperationId.Value]))
                    .ToArray()
            );
        }
        if (playlist.Archived)
        {
            throw new PlaylistException(
                409,
                "playlist_archived",
                "Unarchive the playlist before editing it."
            );
        }

        var newOperations = request
            .Operations.Where(operation => !receipts.ContainsKey(operation.OperationId.Value))
            .ToArray();
        // Closing the write switch must still allow matching committed receipts to replay.
        if (
            !options.EnablePlaylistGuideContent
            && newOperations.Any(PlaylistValidation.UsesGuideContent)
        )
        {
            throw GuideContentDisabled();
        }

        // Name edits cannot affect entry counts or the description budget.
        var editsOnlyName = newOperations.All(operation =>
            operation.Payload is UpdatePlaylistMetadata { Description.IsPresent: false }
        );
        var editor = editsOnlyName
            ? new PlaylistEditor(Copy(playlist), [], [], [])
            : await LoadEditorAsync(playlist, newOperations, cancellationToken);
        var results = new List<PlaylistOperationResult>();
        foreach (var operation in request.Operations)
        {
            results.Add(
                receipts.TryGetValue(operation.OperationId.Value, out var receipt)
                    ? Result(receipt)
                    : editor.Apply(operation)
            );
        }

        // Intermediate states may exceed quotas. The ordered evaluator already resolved parent
        // deletion and semantic rejections; only the final state is admitted and persisted.
        if (!editsOnlyName)
        {
            await CheckEditedQuotaAsync(playlist, editor, cancellationToken);
        }

        var now = timeProvider.GetUtcNow();
        if (editor.Revisions.Count > 0)
        {
            var trackCount = editsOnlyName
                ? await CountTracksAsync(id, cancellationToken)
                : editor.LiveTrackCount;
            PublishContent(playlist, editor, state, trackCount, now);
        }
        foreach (
            var result in results.Where(result => !receipts.ContainsKey(result.OperationId.Value))
        )
        {
            dbContext.PlaylistReceipts.Add(
                new()
                {
                    Id = result.OperationId.Value,
                    ActorId = actor,
                    PlaylistId = id,
                    Kind = "operation",
                    PayloadHash = hashes[result.OperationId.Value],
                    Status = result.Status,
                    Revision = result.Revision,
                    Changed = result.Revision is not null,
                    CreatedAt = now,
                }
            );
        }

        await ReleaseChangedRanksAsync(cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(1, generation, playlist.Revision, results);
    }

    public async Task<PlaylistArchived> ArchiveAsync(
        UserId actor,
        PlaylistId id,
        ArchivePlaylistRequest request,
        CancellationToken cancellationToken
    )
    {
        PlaylistValidation.Validate(request);
        var hash = PlaylistHasher.Hash(id, request);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            cancellationToken
        );
        var state = await LockOwnerAsync(actor, [request.CommandId.Value], [], cancellationToken);
        var playlist = await LockOwnedPlaylistAsync(actor, id, cancellationToken);
        var generation = await GenerationAsync(cancellationToken);
        var receipt = await dbContext.PlaylistReceipts.FindAsync(
            [request.CommandId.Value],
            cancellationToken
        );
        if (receipt is not null)
        {
            CheckReceipt(receipt, actor, id, hash);
            await transaction.CommitAsync(cancellationToken);
            return new(1, generation, id, receipt.Archived!.Value, receipt.Changed);
        }

        var changed = playlist.Archived != request.Archived;
        var now = timeProvider.GetUtcNow();
        if (changed)
        {
            playlist.Archived = request.Archived;
            playlist.UpdatedAt = now;
            var trackCount = await CountTracksAsync(id, cancellationToken);
            AppendLibrarySummary(state, playlist, trackCount, now);
        }
        dbContext.PlaylistReceipts.Add(
            new()
            {
                Id = request.CommandId.Value,
                ActorId = actor,
                PlaylistId = id,
                Kind = "archive",
                PayloadHash = hash,
                Archived = request.Archived,
                Changed = changed,
                CreatedAt = now,
            }
        );
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(1, generation, id, request.Archived, changed);
    }

    private async Task<LibraryState> LockOwnerAsync(
        UserId actor,
        IEnumerable<string> receipts,
        IEnumerable<string> createdIds,
        CancellationToken cancellationToken
    )
    {
        // Lock actual PostgreSQL advisory keys in the existing helper's global numeric order.
        await advisoryLocks.LockAllAsync(
            receipts
                .Select(id => $"playlist-receipt:{id}")
                .Concat(createdIds.Select(id => $"playlist-id:{id}")),
            cancellationToken
        );
        return await stateStore.GetOrCreateForUpdateAsync(
            actor,
            timeProvider.GetUtcNow(),
            cancellationToken
        );
    }

    private async Task<Playlist> LockOwnedPlaylistAsync(
        UserId ownerId,
        PlaylistId id,
        CancellationToken cancellationToken
    )
    {
        var playlist = await dbContext
            .Playlists.FromSqlInterpolated(
                $"""
                SELECT *
                FROM user_data.playlists
                WHERE id = {id.Value} AND owner_id = {ownerId.Value}
                FOR UPDATE
                """
            )
            .SingleOrDefaultAsync(cancellationToken);

        if (playlist is null)
        {
            throw new PlaylistException(404, "playlist_not_found", "Playlist not found.");
        }

        return playlist;
    }

    private async Task<HashSet<string>> ExistingIdsAsync(
        IEnumerable<string> submitted,
        CancellationToken cancellationToken
    )
    {
        var ids = submitted.ToArray();
        var playlists = ids.Where(id =>
                id.StartsWith("playlist_")
                && !id.StartsWith("playlist_segment_")
                && !id.StartsWith("playlist_entry_")
            )
            .Select(PlaylistId.Parse)
            .ToArray();
        var segments = ids.Where(id => id.StartsWith("playlist_segment_"))
            .Select(PlaylistSegmentId.Parse)
            .ToArray();
        var entries = ids.Where(id => id.StartsWith("playlist_entry_"))
            .Select(PlaylistEntryId.Parse)
            .ToArray();
        var existingPlaylists =
            playlists.Length == 0
                ? []
                : await dbContext
                    .Playlists.Where(row => playlists.Contains(row.Id))
                    .Select(row => row.Id)
                    .ToArrayAsync(cancellationToken);
        var existingSegments =
            segments.Length == 0
                ? []
                : await dbContext
                    .PlaylistSegments.Where(row => segments.Contains(row.Id))
                    .Select(row => row.Id)
                    .ToArrayAsync(cancellationToken);
        var existingEntries =
            entries.Length == 0
                ? []
                : await dbContext
                    .PlaylistEntries.Where(row => entries.Contains(row.Id))
                    .Select(row => row.Id)
                    .ToArrayAsync(cancellationToken);
        return existingPlaylists
            .Select(id => id.Value)
            .Concat(existingSegments.Select(id => id.Value))
            .Concat(existingEntries.Select(id => id.Value))
            .ToHashSet(StringComparer.Ordinal);
    }

    private async Task<PlaylistEditor> LoadEditorAsync(
        Playlist playlist,
        IReadOnlyList<PlaylistOperation> operations,
        CancellationToken cancellationToken
    )
    {
        var segmentTargets = operations
            .Select(operation => operation.Payload)
            .SelectMany(payload =>
                payload switch
                {
                    MovePlaylistSegment move => new[] { move.SegmentId },
                    DeletePlaylistSegment delete => [delete.SegmentId],
                    UpdatePlaylistSegment update => [update.SegmentId],
                    _ => Array.Empty<PlaylistSegmentId>(),
                }
            )
            .ToArray();
        var entryTargets = operations
            .Select(operation => operation.Payload)
            .SelectMany(payload =>
                payload switch
                {
                    MovePlaylistEntry move => new[] { move.EntryId },
                    DeletePlaylistEntry delete => [delete.EntryId],
                    UpdatePlaylistEntry update => [update.EntryId],
                    _ => Array.Empty<PlaylistEntryId>(),
                }
            )
            .ToArray();
        // Live state is bounded by quota. Fetch only referenced tombstones; lifetime retry
        // knowledge must not make each small edit load the entire historical playlist.
        var segments = await dbContext
            .PlaylistSegments.Where(row =>
                row.PlaylistId == playlist.Id && (!row.Deleted || segmentTargets.Contains(row.Id))
            )
            .ToArrayAsync(cancellationToken);
        var entries = await dbContext
            .PlaylistEntries.Where(row =>
                row.PlaylistId == playlist.Id && (!row.Deleted || entryTargets.Contains(row.Id))
            )
            .ToArrayAsync(cancellationToken);
        return new(
            Copy(playlist),
            segments.Select(Copy),
            entries.Select(Copy),
            await ExistingIdsAsync(
                operations.SelectMany(PlaylistValidation.CreatedIds),
                cancellationToken
            )
        );
    }

    private T Copy<T>(T tracked)
        where T : class => (T)dbContext.Entry(tracked).CurrentValues.ToObject();

    private async Task CheckEditedQuotaAsync(
        Playlist before,
        PlaylistEditor editor,
        CancellationToken cancellationToken
    )
    {
        CheckQuota(0, editor.LiveEntryCount, 0);
        editor.CheckGuideQuota();

        // The owner lock protects the previously admitted count. Only growth needs a new sum.
        if (editor.LiveEntryCount <= before.EntryCount)
        {
            return;
        }

        var ownedCount = await dbContext
            .Playlists.Where(row => row.OwnerId == before.OwnerId)
            .SumAsync(row => row.EntryCount, cancellationToken);
        CheckQuota(
            0,
            editor.LiveEntryCount,
            ownedCount - before.EntryCount + editor.LiveEntryCount
        );
    }

    private void PublishContent(
        Playlist playlist,
        PlaylistEditor editor,
        LibraryState state,
        int trackCount,
        DateTimeOffset now
    )
    {
        editor.Playlist.UpdatedAt = now;
        ApplyWorkingState(playlist, editor);
        foreach (var revision in editor.Revisions)
        {
            dbContext.PlaylistRevisions.Add(
                new()
                {
                    PlaylistId = playlist.Id,
                    Revision = revision.Revision,
                    Kind = revision.Kind,
                    ChangeCount = revision.Changes.Count,
                    ChangesJson = JsonSerializer.Serialize(
                        revision.Changes,
                        PlaylistJson.StoredOptions
                    ),
                }
            );
        }
        AppendLibrarySummary(state, playlist, trackCount, now);
    }

    private void ApplyWorkingState(Playlist playlist, PlaylistEditor editor)
    {
        dbContext.Entry(playlist).CurrentValues.SetValues(editor.Playlist);
        var segments = dbContext.PlaylistSegments.Local.ToDictionary(row => row.Id);
        foreach (var row in editor.Segments.Values)
        {
            if (segments.TryGetValue(row.Id, out var tracked))
            {
                dbContext.Entry(tracked).CurrentValues.SetValues(row);
            }
            else
            {
                dbContext.PlaylistSegments.Add(row);
            }
        }
        var entries = dbContext.PlaylistEntries.Local.ToDictionary(row => row.Id);
        foreach (var row in editor.Entries.Values)
        {
            if (entries.TryGetValue(row.Id, out var tracked))
            {
                dbContext.Entry(tracked).CurrentValues.SetValues(row);
            }
            else
            {
                dbContext.PlaylistEntries.Add(row);
            }
        }
    }

    private async Task ReleaseChangedRanksAsync(CancellationToken cancellationToken)
    {
        // Partial unique indexes are immediate. Release affected old ranks before EF inserts
        // or swaps final ranks. NULL is transaction-local staging; every live row is restored
        // before commit. Concurrent readers see the previous or final committed structure.
        dbContext.ChangeTracker.DetectChanges();
        var segments = dbContext
            .ChangeTracker.Entries<PlaylistSegment>()
            .Where(entry => entry.Property(row => row.Rank).IsModified)
            .ToArray();
        var entries = dbContext
            .ChangeTracker.Entries<PlaylistEntry>()
            .Where(entry =>
                entry.Property(row => row.Rank).IsModified
                || entry.Property(row => row.SegmentId).IsModified
            )
            .ToArray();
        if (segments.Length > 0)
        {
            var ids = segments.Select(entry => entry.Entity.Id.Value).ToArray();
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE user_data.playlist_segments SET rank = NULL WHERE id = ANY({ids})",
                cancellationToken
            );
            foreach (var entry in segments)
            {
                entry.Property(row => row.Rank).IsModified = true;
            }
        }
        if (entries.Length > 0)
        {
            var ids = entries.Select(entry => entry.Entity.Id.Value).ToArray();
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE user_data.playlist_entries SET rank = NULL WHERE id = ANY({ids})",
                cancellationToken
            );
            foreach (var entry in entries)
            {
                entry.Property(row => row.Rank).IsModified = true;
            }
        }
    }

    private void AddInitialStructure(CreatePlaylistRequest request)
    {
        var ranks = PlaylistRanks.Between(null, null, request.Segments.Count);
        for (var index = 0; index < request.Segments.Count; index++)
        {
            var segment = request.Segments[index];
            dbContext.PlaylistSegments.Add(
                new()
                {
                    Id = segment.SegmentId,
                    PlaylistId = request.PlaylistId,
                    Description = segment.Description,
                    Rank = ranks[index],
                }
            );
            var entryRanks = PlaylistRanks.Between(null, null, segment.Entries.Count);
            for (var entryIndex = 0; entryIndex < segment.Entries.Count; entryIndex++)
            {
                var entry = segment.Entries[entryIndex];
                dbContext.PlaylistEntries.Add(
                    new()
                    {
                        Id = entry.EntryId,
                        PlaylistId = request.PlaylistId,
                        SegmentId = segment.SegmentId,
                        TrackId = entry.TrackId,
                        Description = entry.Description,
                        Rank = entryRanks[entryIndex],
                    }
                );
            }
        }
    }

    private Task<int> CountTracksAsync(PlaylistId id, CancellationToken cancellationToken) =>
        PlaylistReadService
            .LiveTracks(dbContext)
            .CountAsync(entry => entry.PlaylistId == id, cancellationToken);

    private void AppendLibrarySummary(
        LibraryState state,
        Playlist playlist,
        int trackCount,
        DateTimeOffset now
    )
    {
        state.Revision++;
        state.UpdatedAt = now;
        dbContext.LibraryChanges.Add(
            new()
            {
                UserId = playlist.OwnerId,
                Revision = state.Revision,
                ChangeType = "playlist_summary",
                ChangedAt = now,
                PlaylistJson = JsonSerializer.Serialize(
                    new PlaylistSummary(
                        playlist.Id,
                        playlist.Name,
                        playlist.EntryCount,
                        playlist.Archived,
                        playlist.Revision,
                        playlist.UpdatedAt
                    )
                    {
                        TrackCount = trackCount,
                    },
                    PlaylistJson.StoredOptions
                ),
            }
        );
    }

    private Task<string> GenerationAsync(CancellationToken cancellationToken) =>
        dbContext.HistoryGenerations.Select(row => row.Value).SingleAsync(cancellationToken);

    private static PlaylistOperationResult Result(PlaylistReceipt receipt) =>
        new(PlaylistOperationId.Parse(receipt.Id), receipt.Status!, receipt.Revision);

    private static bool Matches(
        PlaylistReceipt receipt,
        UserId actor,
        PlaylistId id,
        byte[] hash
    ) =>
        receipt.ActorId == actor
        && receipt.PlaylistId == id
        && CryptographicOperations.FixedTimeEquals(receipt.PayloadHash, hash);

    private static void CheckReceipt(
        PlaylistReceipt receipt,
        UserId actor,
        PlaylistId id,
        byte[] hash
    )
    {
        if (!Matches(receipt, actor, id, hash))
        {
            throw new PlaylistException(
                409,
                "idempotency_conflict",
                "The command ID was already used by another actor, playlist or payload."
            );
        }
    }

    private static void CheckQuota(int playlists, int entries, int ownedEntries)
    {
        if (
            playlists > PlaylistValidation.MaximumPlaylists
            || entries > PlaylistValidation.MaximumEntries
            || ownedEntries > PlaylistValidation.MaximumOwnedEntries
        )
        {
            throw new PlaylistException(
                422,
                "quota_exceeded",
                "The final state exceeds the playlist or entry quota."
            );
        }
    }

    private static PlaylistException GuideContentDisabled() =>
        new(422, "playlist_guide_content_disabled", "Playlist guide content writes are disabled.");
}
