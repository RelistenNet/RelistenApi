using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Relisten.Accounts.Contracts.Playlists;
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
    TimeProvider timeProvider
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
        AppendLibrarySummary(state, playlist, now);
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
        var playlist = await LockPlaylistAsync(actor, id, cancellationToken);
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
        var editor = await LoadEditorAsync(playlist, newOperations, createdIds, cancellationToken);
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
        var ownedCount = await dbContext
            .Playlists.Where(row => row.OwnerId == actor)
            .SumAsync(row => row.EntryCount, cancellationToken);
        CheckQuota(
            0,
            editor.LiveEntryCount,
            ownedCount - playlist.EntryCount + editor.LiveEntryCount
        );
        var now = timeProvider.GetUtcNow();
        PublishContent(playlist, editor, state, now);
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
        var playlist = await LockPlaylistAsync(actor, id, cancellationToken);
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
            AppendLibrarySummary(state, playlist, now);
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
        return await stateStore.GetOrCreateAsync(
            actor,
            timeProvider.GetUtcNow(),
            LibraryStateLockMode.ExclusiveWrite,
            cancellationToken
        );
    }

    private async Task<Playlist> LockPlaylistAsync(
        UserId actor,
        PlaylistId id,
        CancellationToken cancellationToken
    )
    {
        var playlist = await dbContext
            .Playlists.FromSqlInterpolated(
                $"SELECT * FROM user_data.playlists WHERE id = {id.Value} FOR UPDATE"
            )
            .SingleOrDefaultAsync(cancellationToken);
        if (playlist is null || playlist.OwnerId != actor)
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
        var existingPlaylists = await dbContext
            .Playlists.Where(row => playlists.Contains(row.Id))
            .Select(row => row.Id)
            .ToArrayAsync(cancellationToken);
        var existingSegments = await dbContext
            .PlaylistSegments.Where(row => segments.Contains(row.Id))
            .Select(row => row.Id)
            .ToArrayAsync(cancellationToken);
        var existingEntries = await dbContext
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
        string[] createdIds,
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
            await ExistingIdsAsync(createdIds, cancellationToken)
        );
    }

    private T Copy<T>(T tracked)
        where T : class => (T)dbContext.Entry(tracked).CurrentValues.ToObject();

    private void PublishContent(
        Playlist playlist,
        PlaylistEditor editor,
        LibraryState state,
        DateTimeOffset now
    )
    {
        if (editor.Revisions.Count == 0)
        {
            return;
        }
        editor.Playlist.EntryCount = editor.LiveEntryCount;
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
                    ChangesJson = JsonSerializer.Serialize(revision.Changes, PlaylistJson.Options),
                }
            );
        }
        AppendLibrarySummary(state, playlist, now);
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
            .Where(entry => entry.State == EntityState.Modified)
            .ToArray();
        var entries = dbContext
            .ChangeTracker.Entries<PlaylistEntry>()
            .Where(entry => entry.State == EntityState.Modified)
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
                        Rank = entryRanks[entryIndex],
                    }
                );
            }
        }
    }

    private void AppendLibrarySummary(LibraryState state, Playlist playlist, DateTimeOffset now)
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
                    ),
                    PlaylistJson.Options
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
}
