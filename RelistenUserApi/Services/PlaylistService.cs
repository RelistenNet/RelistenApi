using Dapper;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Npgsql;
using Relisten.UserApi.Models;
using Relisten.UserApi.Serialization;

namespace Relisten.UserApi.Services;

public sealed class PlaylistService
{
    private const int MaxPlaylistNameLength = 200;
    private const int MaxEntriesPerPlaylist = 2000;

    private readonly UserApiDbService _db;
    private readonly CatalogSourceRangeService _catalogSourceRanges;
    private readonly ShortIdService _shortIds;

    public PlaylistService(
        UserApiDbService db,
        CatalogSourceRangeService catalogSourceRanges,
        ShortIdService shortIds)
    {
        _db = db;
        _catalogSourceRanges = catalogSourceRanges;
        _shortIds = shortIds;
    }

    public async Task<IReadOnlyList<PlaylistResponse>> ListForUser(Guid userUuid)
    {
        await using var connection = _db.CreateConnection();
        var playlists = await connection.QueryAsync<PlaylistRecord>(
            """
            SELECT
                id AS "PlaylistUuid",
                short_id AS "ShortId",
                owner_id AS "OwnerUserUuid",
                name AS "Name",
                description AS "Description",
                visibility AS "Visibility",
                current_revision AS "CurrentRevision",
                archived_at AS "ArchivedAt",
                created_at AS "CreatedAt",
                updated_at AS "UpdatedAt"
            FROM user_data.playlists
            WHERE archived_at IS NULL
              AND (
                  owner_id = @UserUuid
                  OR EXISTS (
                      SELECT 1
                      FROM user_data.playlist_collaborators c
                      WHERE c.playlist_id = playlists.id
                        AND c.user_id = @UserUuid
                        AND c.accepted_at IS NOT NULL
                        AND c.revoked_at IS NULL
                  )
                  OR EXISTS (
                      SELECT 1
                      FROM user_data.playlist_followers f
                      WHERE f.playlist_id = playlists.id
                        AND f.user_id = @UserUuid
                        AND f.unfollowed_at IS NULL
                  )
              )
            ORDER BY updated_at DESC
            """,
            new { UserUuid = userUuid });

        var playlistList = playlists.ToList();
        if (playlistList.Count == 0)
        {
            return [];
        }

        var entriesByPlaylist = (await LoadEntriesForPlaylists(
                connection,
                playlistList.Select(playlist => playlist.PlaylistUuid).ToArray(),
                transaction: null))
            .GroupBy(entry => entry.PlaylistUuid)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<PlaylistEntryRecord>)group.ToList());

        return playlistList
            .Select(playlist => ToResponse(new PlaylistSnapshot
            {
                Playlist = playlist,
                Entries = entriesByPlaylist.GetValueOrDefault(playlist.PlaylistUuid, [])
            }))
            .ToList();
    }

    public async Task<PlaylistResponse> Create(Guid ownerUserUuid, CreatePlaylistRequest request)
    {
        var now = DateTimeOffset.UtcNow;
        var playlistUuid = request.PlaylistUuid ?? UserDataUuid.New();
        var name = NormalizeName(request.Name);
        var description = NormalizeDescription(request.Description);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await using var connection = _db.CreateConnection();
            try
            {
                await connection.ExecuteAsync(
                    """
                    INSERT INTO user_data.playlists
                        (id, short_id, owner_id, name, description, visibility, current_revision,
                         moderation_status, created_at, updated_at)
                    VALUES
                        (@PlaylistUuid, @ShortId, @OwnerUserUuid, @Name, @Description, 'private', 0,
                         'approved', @Now, @Now)
                    """,
                    new
                    {
                        PlaylistUuid = playlistUuid,
                        ShortId = _shortIds.Generate(),
                        OwnerUserUuid = ownerUserUuid,
                        Name = name,
                        Description = description,
                        Now = now
                    });

                return await GetForOwner(ownerUserUuid, playlistUuid)
                    ?? throw new InvalidOperationException("Created playlist could not be loaded.");
            }
            catch (PostgresException ex) when (
                ex.SqlState == PostgresErrorCodes.UniqueViolation &&
                string.Equals(ex.ConstraintName, "playlists_short_id_key", StringComparison.Ordinal))
            {
                continue;
            }
            catch (PostgresException ex) when (
                ex.SqlState == PostgresErrorCodes.UniqueViolation &&
                string.Equals(ex.ConstraintName, "playlists_pkey", StringComparison.Ordinal))
            {
                throw new PlaylistOperationException("playlist_uuid_conflict");
            }
        }

        throw new PlaylistOperationException("short_id_collision");
    }

    public async Task<PlaylistResponse?> GetForOwner(Guid ownerUserUuid, Guid playlistUuid)
    {
        await using var connection = _db.CreateConnection();
        var snapshot = await LoadSnapshot(connection, ownerUserUuid, playlistUuid, transaction: null);
        return snapshot == null ? null : ToResponse(snapshot);
    }

    public async Task<PlaylistOperationResponse?> ApplyOperation(
        Guid userUuid,
        Guid playlistUuid,
        PlaylistOperationRequest request)
    {
        if (request.IdempotencyKey == Guid.Empty)
        {
            throw new PlaylistOperationException("invalid_idempotency_key");
        }

        var operationJson = JsonConvert.SerializeObject(request, UserLibraryJson.SerializerSettings);
        await using var connection = _db.CreateConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await LockIdempotencyKey(connection, transaction, request.IdempotencyKey);

        var existing = await connection.QuerySingleOrDefaultAsync<ExistingOperationRow>(
            """
            SELECT
                playlist_id AS "PlaylistUuid",
                operation::text AS "OperationJson",
                result_revision AS "ResultRevision",
                result_status AS "ResultStatus"
            FROM user_data.playlist_edit_log
            WHERE idempotency_key = @IdempotencyKey
            """,
            new { request.IdempotencyKey },
            transaction);

        if (existing != null)
        {
            if (existing.PlaylistUuid != playlistUuid)
            {
                throw new PlaylistOperationException("idempotency_key_conflict");
            }

            if (!JsonOperationsEqual(existing.OperationJson, operationJson))
            {
                throw new PlaylistOperationException("idempotency_key_conflict");
            }

            if (!await CanWritePlaylist(connection, transaction, userUuid, playlistUuid))
            {
                await transaction.RollbackAsync();
                return null;
            }

            var replaySnapshot = await LoadSnapshot(connection, playlistUuid, transaction);
            await transaction.CommitAsync();
            return replaySnapshot == null
                ? null
                : new PlaylistOperationResponse
                {
                    ResultRevision = existing.ResultRevision,
                    ResultStatus = ReplayStatus(existing.ResultStatus),
                    Playlist = ToResponse(replaySnapshot)
                };
        }

        var playlist = await LockPlaylistForWriter(connection, transaction, userUuid, playlistUuid);
        if (playlist == null)
        {
            await transaction.RollbackAsync();
            return null;
        }

        var entries = await LoadEntries(connection, playlistUuid, transaction);
        var mutation = await BuildMutation(request, userUuid, playlistUuid, entries);

        if (mutation.FinalEntries.Count > MaxEntriesPerPlaylist)
        {
            throw new PlaylistOperationException("rejected_limit");
        }

        if (mutation.ResultStatus == "applied" && mutation.NewBlockUuid.HasValue)
        {
            await InsertBlock(
                connection,
                transaction,
                mutation.NewBlockUuid.Value,
                playlistUuid,
                userUuid,
                DateTimeOffset.UtcNow);
        }

        foreach (var entry in mutation.NewEntries)
        {
            await InsertEntry(connection, transaction, entry.ToRecord(TemporaryPosition(entry.PlaylistEntryUuid)));
        }

        var resultRevision = playlist.CurrentRevision;
        if (mutation.ResultStatus == "applied")
        {
            await RewritePlaylistEntries(connection, transaction, playlistUuid, mutation.FinalEntries);
            await DeleteEmptyBlocks(connection, transaction, playlistUuid);
            resultRevision++;
            await connection.ExecuteAsync(
                """
                UPDATE user_data.playlists
                SET current_revision = @ResultRevision,
                    updated_at = @Now,
                    sync_version = nextval('user_data.user_sync_version_seq')
                WHERE id = @PlaylistUuid
                """,
                new
                {
                    PlaylistUuid = playlistUuid,
                    ResultRevision = resultRevision,
                    Now = DateTimeOffset.UtcNow
                },
                transaction);
        }

        await connection.ExecuteAsync(
            """
            INSERT INTO user_data.playlist_edit_log
                (id, playlist_id, user_id, operation, idempotency_key, base_revision,
                 result_revision, result_status, created_at)
            VALUES
                (@LogUuid, @PlaylistUuid, @UserUuid, CAST(@OperationJson AS jsonb), @IdempotencyKey,
                 @BaseRevision, @ResultRevision, @ResultStatus, @Now)
            """,
            new
            {
                LogUuid = UserDataUuid.New(),
                PlaylistUuid = playlistUuid,
                UserUuid = userUuid,
                OperationJson = operationJson,
                request.IdempotencyKey,
                request.BaseRevision,
                ResultRevision = resultRevision,
                mutation.ResultStatus,
                Now = DateTimeOffset.UtcNow
            },
            transaction);

        var snapshot = await LoadSnapshot(connection, playlistUuid, transaction)
            ?? throw new InvalidOperationException("Updated playlist could not be loaded.");
        await transaction.CommitAsync();

        return new PlaylistOperationResponse
        {
            ResultRevision = resultRevision,
            ResultStatus = mutation.ResultStatus,
            Playlist = ToResponse(snapshot)
        };
    }

    private async Task<PlaylistMutation> BuildMutation(
        PlaylistOperationRequest request,
        Guid userUuid,
        Guid playlistUuid,
        IReadOnlyList<PlaylistEntryRecord> entries)
    {
        return request.Op switch
        {
            "add_track" => BuildSingleEntry(request, userUuid, playlistUuid, entries),
            "add_tracks_as_block" => BuildBlockEntries(request, userUuid, playlistUuid, entries),
            "add_source_range_as_block" => await BuildSourceRangeBlockEntries(request, userUuid, playlistUuid, entries),
            "move_entry" => MoveEntry(request, entries),
            "move_block" => MoveBlock(request, entries),
            _ => throw new PlaylistOperationException("unsupported_operation")
        };
    }

    private static PlaylistMutation BuildSingleEntry(
        PlaylistOperationRequest request,
        Guid userUuid,
        Guid playlistUuid,
        IReadOnlyList<PlaylistEntryRecord> entries)
    {
        if (request.EntryUuid == null ||
            request.SourceTrackUuid == null ||
            request.BlockUuid != null ||
            request.EntryUuids != null ||
            request.SourceTrackUuids != null ||
            request.SourceUuid != null ||
            request.StartTrackPosition != null ||
            request.EndTrackPosition != null ||
            HasBlockPlacement(request.Placement))
        {
            throw new PlaylistOperationException("invalid_operation");
        }

        ValidateNewClientIds([request.EntryUuid.Value], blockUuid: null, entries);

        var newEntry = NewEntry(
                playlistUuid,
                request.EntryUuid.Value,
                request.SourceTrackUuid.Value,
                blockUuid: null,
                blockPosition: null,
                position: TemporaryPosition(request.EntryUuid.Value),
                userUuid);
        return ApplyAdd(entries, [newEntry], request.Placement, newBlockUuid: null);
    }

    private static PlaylistMutation BuildBlockEntries(
        PlaylistOperationRequest request,
        Guid userUuid,
        Guid playlistUuid,
        IReadOnlyList<PlaylistEntryRecord> entries)
    {
        if (request.BlockUuid == null ||
            request.EntryUuids is not { Count: > 0 } ||
            request.SourceTrackUuids is not { Count: > 0 } ||
            request.EntryUuids.Count != request.SourceTrackUuids.Count ||
            request.EntryUuid != null ||
            request.SourceTrackUuid != null ||
            request.SourceUuid != null ||
            request.StartTrackPosition != null ||
            request.EndTrackPosition != null ||
            HasBlockPlacement(request.Placement))
        {
            throw new PlaylistOperationException("invalid_operation");
        }

        ValidateNewClientIds(request.EntryUuids, request.BlockUuid, entries);

        var newEntries = request.EntryUuids
            .Select((entryUuid, index) => NewEntry(
                playlistUuid,
                entryUuid,
                request.SourceTrackUuids[index],
                request.BlockUuid.Value,
                index,
                TemporaryPosition(entryUuid),
                userUuid))
            .ToList();
        return ApplyAdd(entries, newEntries, request.Placement, request.BlockUuid.Value);
    }

    private async Task<PlaylistMutation> BuildSourceRangeBlockEntries(
        PlaylistOperationRequest request,
        Guid userUuid,
        Guid playlistUuid,
        IReadOnlyList<PlaylistEntryRecord> entries)
    {
        if (request.BlockUuid == null ||
            request.SourceUuid == null ||
            request.StartTrackPosition == null ||
            request.EndTrackPosition == null ||
            request.EntryUuid != null ||
            request.SourceTrackUuid != null ||
            request.SourceTrackUuids != null ||
            HasBlockPlacement(request.Placement))
        {
            throw new PlaylistOperationException("invalid_operation");
        }

        var sourceTrackUuids = await _catalogSourceRanges.ResolveSourceTrackUuids(
            request.SourceUuid.Value,
            request.StartTrackPosition.Value,
            request.EndTrackPosition.Value);
        var entryUuids = request.EntryUuids ?? sourceTrackUuids.Select(_ => UserDataUuid.New()).ToList();
        if (entryUuids.Count != sourceTrackUuids.Count)
        {
            throw new PlaylistOperationException("invalid_operation");
        }

        ValidateNewClientIds(entryUuids, request.BlockUuid, entries);

        var newEntries = entryUuids
            .Select((entryUuid, index) => NewEntry(
                playlistUuid,
                entryUuid,
                sourceTrackUuids[index],
                request.BlockUuid.Value,
                index,
                TemporaryPosition(entryUuid),
                userUuid))
            .ToList();
        return ApplyAdd(entries, newEntries, request.Placement, request.BlockUuid.Value);
    }

    private static PlaylistMutation MoveEntry(
        PlaylistOperationRequest request,
        IReadOnlyList<PlaylistEntryRecord> entries)
    {
        if (request.EntryUuid == null ||
            request.SourceTrackUuid != null ||
            request.BlockUuid != null ||
            request.EntryUuids != null ||
            request.SourceTrackUuids != null ||
            request.SourceUuid != null ||
            request.StartTrackPosition != null ||
            request.EndTrackPosition != null ||
            request.Placement == null)
        {
            throw new PlaylistOperationException("invalid_operation");
        }

        var unchangedEntries = ToMutableEntries(entries);
        var mutableEntries = ToMutableEntries(entries);
        var entry = mutableEntries.SingleOrDefault(candidate => candidate.PlaylistEntryUuid == request.EntryUuid);
        if (entry == null)
        {
            return PlaylistMutation.NoStateChange("noop_entry_missing", unchangedEntries);
        }

        RejectMoveAnchors(request.Placement, [request.EntryUuid.Value]);
        mutableEntries.Remove(entry);
        if (request.Placement.TargetBlockUuid.HasValue)
        {
            entry.BlockUuid = request.Placement.TargetBlockUuid;
            var blockIndex = PlacementIndexInsideBlock(
                mutableEntries,
                request.Placement.TargetBlockUuid.Value,
                request.Placement.TargetBlockIndex);
            mutableEntries.Insert(blockIndex, entry);
        }
        else
        {
            entry.BlockUuid = null;
            entry.BlockPosition = null;
            var insertionIndex = PlacementIndex(mutableEntries, request.Placement);
            mutableEntries.Insert(insertionIndex, entry);
        }

        return CanonicalizeOrReject(mutableEntries, unchangedEntries: unchangedEntries);
    }

    private static PlaylistMutation MoveBlock(
        PlaylistOperationRequest request,
        IReadOnlyList<PlaylistEntryRecord> entries)
    {
        if (request.BlockUuid == null ||
            request.EntryUuid != null ||
            request.SourceTrackUuid != null ||
            request.EntryUuids != null ||
            request.SourceTrackUuids != null ||
            request.SourceUuid != null ||
            request.StartTrackPosition != null ||
            request.EndTrackPosition != null ||
            request.Placement == null ||
            HasBlockPlacement(request.Placement))
        {
            throw new PlaylistOperationException("invalid_operation");
        }

        var unchangedEntries = ToMutableEntries(entries);
        var mutableEntries = ToMutableEntries(entries);
        var blockEntries = mutableEntries
            .Where(entry => entry.BlockUuid == request.BlockUuid)
            .OrderBy(entry => entry.BlockPosition)
            .ThenBy(entry => entry.Position)
            .ToList();
        if (blockEntries.Count == 0)
        {
            return PlaylistMutation.NoStateChange("noop_block_empty", unchangedEntries);
        }

        RejectMoveAnchors(request.Placement, blockEntries.Select(entry => entry.PlaylistEntryUuid));

        foreach (var entry in blockEntries)
        {
            mutableEntries.Remove(entry);
        }

        var insertionIndex = PlacementIndex(mutableEntries, request.Placement);
        mutableEntries.InsertRange(insertionIndex, blockEntries);
        return CanonicalizeOrReject(mutableEntries, unchangedEntries: unchangedEntries);
    }

    private static PlaylistMutation ApplyAdd(
        IReadOnlyList<PlaylistEntryRecord> entries,
        IReadOnlyList<PlaylistEntryMutation> newEntries,
        PlaylistPlacementRequest? placement,
        Guid? newBlockUuid)
    {
        var unchangedEntries = ToMutableEntries(entries);
        var finalEntries = ToMutableEntries(entries);
        finalEntries.InsertRange(PlacementIndex(finalEntries, placement), newEntries);
        return CanonicalizeOrReject(finalEntries, newEntries, newBlockUuid, unchangedEntries);
    }

    private static PlaylistEntryMutation NewEntry(
        Guid playlistUuid,
        Guid entryUuid,
        Guid sourceTrackUuid,
        Guid? blockUuid,
        int? blockPosition,
        string position,
        Guid userUuid)
    {
        var now = DateTimeOffset.UtcNow;
        return new PlaylistEntryMutation
        {
            PlaylistEntryUuid = entryUuid,
            PlaylistUuid = playlistUuid,
            SourceTrackUuid = sourceTrackUuid,
            BlockUuid = blockUuid,
            BlockPosition = blockPosition,
            Position = position,
            AddedByUserUuid = userUuid,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static async Task RewritePlaylistEntries(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid playlistUuid,
        IReadOnlyList<PlaylistEntryMutation> entries)
    {
        var now = DateTimeOffset.UtcNow;
        await connection.ExecuteAsync(
            """
            UPDATE user_data.playlist_entries
            SET position = CONCAT('~rewrite~', REPLACE(id::text, '-', '')),
                block_uuid = NULL,
                block_position = NULL,
                updated_at = @Now
            WHERE playlist_id = @PlaylistUuid
            """,
            new { PlaylistUuid = playlistUuid, Now = now },
            transaction);

        foreach (var entry in entries)
        {
            await connection.ExecuteAsync(
                """
                UPDATE user_data.playlist_entries
                SET position = @Position,
                    block_uuid = @BlockUuid,
                    block_position = @BlockPosition,
                    updated_at = @Now
                WHERE id = @PlaylistEntryUuid
                  AND playlist_id = @PlaylistUuid
                """,
                new
                {
                    entry.PlaylistEntryUuid,
                    entry.PlaylistUuid,
                    entry.Position,
                    entry.BlockUuid,
                    entry.BlockPosition,
                    Now = now
                },
                transaction);
        }
    }

    private static Task DeleteEmptyBlocks(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid playlistUuid)
    {
        return connection.ExecuteAsync(
            """
            DELETE FROM user_data.playlist_blocks b
            WHERE b.playlist_id = @PlaylistUuid
              AND NOT EXISTS (
                  SELECT 1
                  FROM user_data.playlist_entries e
                  WHERE e.playlist_id = b.playlist_id
                    AND e.block_uuid = b.id
              )
            """,
            new { PlaylistUuid = playlistUuid },
            transaction);
    }

    private static async Task InsertEntry(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PlaylistEntryRecord entry)
    {
        try
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO user_data.playlist_entries
                    (id, playlist_id, source_track_uuid, block_uuid, position, block_position,
                     added_by, created_at, updated_at)
                VALUES
                    (@PlaylistEntryUuid, @PlaylistUuid, @SourceTrackUuid, @BlockUuid, @Position,
                     @BlockPosition, @AddedByUserUuid, @CreatedAt, @UpdatedAt)
                """,
                entry,
                transaction);
        }
        catch (PostgresException ex) when (
            ex.SqlState == PostgresErrorCodes.UniqueViolation &&
            string.Equals(ex.ConstraintName, "playlist_entries_pkey", StringComparison.Ordinal))
        {
            throw new PlaylistOperationException("entry_uuid_conflict");
        }
    }

    private static async Task InsertBlock(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid blockUuid,
        Guid playlistUuid,
        Guid userUuid,
        DateTimeOffset now)
    {
        try
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO user_data.playlist_blocks
                    (id, playlist_id, created_by, created_at)
                VALUES
                    (@BlockUuid, @PlaylistUuid, @UserUuid, @Now)
                """,
                new
                {
                    BlockUuid = blockUuid,
                    PlaylistUuid = playlistUuid,
                    UserUuid = userUuid,
                    Now = now
                },
                transaction);
        }
        catch (PostgresException ex) when (
            ex.SqlState == PostgresErrorCodes.UniqueViolation &&
            string.Equals(ex.ConstraintName, "playlist_blocks_pkey", StringComparison.Ordinal))
        {
            throw new PlaylistOperationException("block_uuid_conflict");
        }
    }

    private static Task LockIdempotencyKey(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid idempotencyKey)
    {
        var bytes = idempotencyKey.ToByteArray();
        var lockKey = BitConverter.ToInt64(bytes, 0);
        return connection.ExecuteAsync(
            "SELECT pg_advisory_xact_lock(@LockKey)",
            new { LockKey = lockKey },
            transaction);
    }

    private static void ValidateNewClientIds(
        IReadOnlyCollection<Guid> requestedEntryUuids,
        Guid? blockUuid,
        IReadOnlyList<PlaylistEntryRecord> existingEntries)
    {
        if (requestedEntryUuids.Any(entryUuid => entryUuid == Guid.Empty) ||
            blockUuid == Guid.Empty)
        {
            throw new PlaylistOperationException("invalid_operation");
        }

        if (requestedEntryUuids.Count != requestedEntryUuids.Distinct().Count() ||
            requestedEntryUuids.Any(entryUuid =>
                existingEntries.Any(existing => existing.PlaylistEntryUuid == entryUuid)))
        {
            throw new PlaylistOperationException("entry_uuid_conflict");
        }

        if (blockUuid.HasValue &&
            existingEntries.Any(existing => existing.BlockUuid == blockUuid))
        {
            throw new PlaylistOperationException("block_uuid_conflict");
        }
    }

    private static PlaylistMutation CanonicalizeOrReject(
        List<PlaylistEntryMutation> finalEntries,
        IReadOnlyList<PlaylistEntryMutation>? newEntries = null,
        Guid? newBlockUuid = null,
        IReadOnlyList<PlaylistEntryMutation>? unchangedEntries = null)
    {
        if (!BlocksAreContiguous(finalEntries))
        {
            return PlaylistMutation.NoStateChange("rejected_contiguity", unchangedEntries ?? []);
        }

        for (var index = 0; index < finalEntries.Count; index++)
        {
            finalEntries[index].Position = PositionForOrdinal(index);
        }

        var blockPositions = new Dictionary<Guid, int>();
        foreach (var entry in finalEntries)
        {
            if (!entry.BlockUuid.HasValue)
            {
                entry.BlockPosition = null;
                continue;
            }

            var nextPosition = blockPositions.GetValueOrDefault(entry.BlockUuid.Value);
            entry.BlockPosition = nextPosition;
            blockPositions[entry.BlockUuid.Value] = nextPosition + 1;
        }

        return PlaylistMutation.Applied(finalEntries, newEntries ?? [], newBlockUuid);
    }

    private static bool BlocksAreContiguous(IReadOnlyList<PlaylistEntryMutation> entries)
    {
        var seenBlocks = new HashSet<Guid>();
        Guid? currentBlockUuid = null;

        foreach (var entry in entries)
        {
            if (!entry.BlockUuid.HasValue)
            {
                currentBlockUuid = null;
                continue;
            }

            if (currentBlockUuid == entry.BlockUuid)
            {
                continue;
            }

            if (seenBlocks.Contains(entry.BlockUuid.Value))
            {
                return false;
            }

            seenBlocks.Add(entry.BlockUuid.Value);
            currentBlockUuid = entry.BlockUuid;
        }

        return true;
    }

    private static List<PlaylistEntryMutation> ToMutableEntries(IReadOnlyList<PlaylistEntryRecord> entries)
    {
        return entries
            .Select(entry => new PlaylistEntryMutation
            {
                PlaylistEntryUuid = entry.PlaylistEntryUuid,
                PlaylistUuid = entry.PlaylistUuid,
                SourceTrackUuid = entry.SourceTrackUuid,
                BlockUuid = entry.BlockUuid,
                BlockPosition = entry.BlockPosition,
                Position = entry.Position,
                AddedByUserUuid = entry.AddedByUserUuid,
                CreatedAt = entry.CreatedAt,
                UpdatedAt = entry.UpdatedAt
            })
            .ToList();
    }

    private static int PlacementIndex(
        IReadOnlyList<PlaylistEntryMutation> entries,
        PlaylistPlacementRequest? placement)
    {
        if (placement == null)
        {
            return entries.Count;
        }

        if (placement.TargetBlockIndex.HasValue)
        {
            throw new PlaylistOperationException("invalid_placement");
        }

        var afterIndex = placement.AfterEntryUuid.HasValue
            ? FindEntryIndex(entries, placement.AfterEntryUuid.Value)
            : null;
        var beforeIndex = placement.BeforeEntryUuid.HasValue
            ? FindEntryIndex(entries, placement.BeforeEntryUuid.Value)
            : null;

        if (afterIndex.HasValue && beforeIndex.HasValue && afterIndex.Value < beforeIndex.Value)
        {
            return afterIndex.Value + 1;
        }

        if (beforeIndex.HasValue)
        {
            return beforeIndex.Value;
        }

        if (afterIndex.HasValue)
        {
            return afterIndex.Value + 1;
        }

        return entries.Count;
    }

    private static int PlacementIndexInsideBlock(
        IReadOnlyList<PlaylistEntryMutation> entries,
        Guid blockUuid,
        int? targetBlockIndex)
    {
        if (blockUuid == Guid.Empty || targetBlockIndex < 0)
        {
            throw new PlaylistOperationException("invalid_placement");
        }

        var blockEntryIndexes = entries
            .Select((entry, index) => new { entry, index })
            .Where(item => item.entry.BlockUuid == blockUuid)
            .Select(item => item.index)
            .ToList();
        if (blockEntryIndexes.Count == 0)
        {
            throw new PlaylistOperationException("invalid_placement");
        }

        var insertionIndexInBlock = targetBlockIndex ?? blockEntryIndexes.Count;
        if (insertionIndexInBlock > blockEntryIndexes.Count)
        {
            throw new PlaylistOperationException("invalid_placement");
        }

        return insertionIndexInBlock == blockEntryIndexes.Count
            ? blockEntryIndexes[^1] + 1
            : blockEntryIndexes[insertionIndexInBlock];
    }

    private static int? FindEntryIndex(
        IReadOnlyList<PlaylistEntryMutation> entries,
        Guid entryUuid)
    {
        for (var index = 0; index < entries.Count; index++)
        {
            if (entries[index].PlaylistEntryUuid == entryUuid)
            {
                return index;
            }
        }

        return null;
    }

    private static void RejectMoveAnchors(
        PlaylistPlacementRequest? placement,
        IEnumerable<Guid> movingEntryUuids)
    {
        if (placement == null)
        {
            return;
        }

        var movingSet = movingEntryUuids.ToHashSet();
        if ((placement.AfterEntryUuid.HasValue && movingSet.Contains(placement.AfterEntryUuid.Value)) ||
            (placement.BeforeEntryUuid.HasValue && movingSet.Contains(placement.BeforeEntryUuid.Value)))
        {
            throw new PlaylistOperationException("invalid_placement");
        }
    }

    private static bool HasBlockPlacement(PlaylistPlacementRequest? placement)
    {
        return placement?.TargetBlockUuid != null || placement?.TargetBlockIndex != null;
    }

    private static async Task<PlaylistSnapshot?> LoadSnapshot(
        NpgsqlConnection connection,
        Guid ownerUserUuid,
        Guid playlistUuid,
        NpgsqlTransaction? transaction)
    {
        var playlist = await connection.QuerySingleOrDefaultAsync<PlaylistRecord>(
            """
            SELECT
                id AS "PlaylistUuid",
                short_id AS "ShortId",
                owner_id AS "OwnerUserUuid",
                name AS "Name",
                description AS "Description",
                visibility AS "Visibility",
                current_revision AS "CurrentRevision",
                archived_at AS "ArchivedAt",
                created_at AS "CreatedAt",
                updated_at AS "UpdatedAt"
            FROM user_data.playlists
            WHERE id = @PlaylistUuid AND owner_id = @OwnerUserUuid AND archived_at IS NULL
            """,
            new { PlaylistUuid = playlistUuid, OwnerUserUuid = ownerUserUuid },
            transaction);

        if (playlist == null)
        {
            return null;
        }

        return new PlaylistSnapshot
        {
            Playlist = playlist,
            Entries = await LoadEntries(connection, playlistUuid, transaction)
        };
    }

    private static async Task<PlaylistSnapshot?> LoadSnapshot(
        NpgsqlConnection connection,
        Guid playlistUuid,
        NpgsqlTransaction? transaction)
    {
        var playlist = await connection.QuerySingleOrDefaultAsync<PlaylistRecord>(
            """
            SELECT
                id AS "PlaylistUuid",
                short_id AS "ShortId",
                owner_id AS "OwnerUserUuid",
                name AS "Name",
                description AS "Description",
                visibility AS "Visibility",
                current_revision AS "CurrentRevision",
                archived_at AS "ArchivedAt",
                created_at AS "CreatedAt",
                updated_at AS "UpdatedAt"
            FROM user_data.playlists
            WHERE id = @PlaylistUuid AND archived_at IS NULL
            """,
            new { PlaylistUuid = playlistUuid },
            transaction);

        if (playlist == null)
        {
            return null;
        }

        return new PlaylistSnapshot
        {
            Playlist = playlist,
            Entries = await LoadEntries(connection, playlistUuid, transaction)
        };
    }

    private static Task<PlaylistRecord?> LockPlaylistForWriter(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid userUuid,
        Guid playlistUuid)
    {
        return connection.QuerySingleOrDefaultAsync<PlaylistRecord>(
            """
            SELECT
                id AS "PlaylistUuid",
                short_id AS "ShortId",
                owner_id AS "OwnerUserUuid",
                name AS "Name",
                description AS "Description",
                visibility AS "Visibility",
                current_revision AS "CurrentRevision",
                archived_at AS "ArchivedAt",
                created_at AS "CreatedAt",
                updated_at AS "UpdatedAt"
            FROM user_data.playlists
            WHERE id = @PlaylistUuid
              AND archived_at IS NULL
              AND (
                  owner_id = @UserUuid
                  OR EXISTS (
                      SELECT 1
                      FROM user_data.playlist_collaborators c
                      WHERE c.playlist_id = playlists.id
                        AND c.user_id = @UserUuid
                        AND c.role = 'editor'
                        AND c.accepted_at IS NOT NULL
                        AND c.revoked_at IS NULL
                  )
              )
            FOR UPDATE
            """,
            new { PlaylistUuid = playlistUuid, UserUuid = userUuid },
            transaction);
    }

    private static async Task<bool> CanWritePlaylist(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid userUuid,
        Guid playlistUuid)
    {
        return await connection.QuerySingleOrDefaultAsync<int?>(
            """
            SELECT 1
            FROM user_data.playlists
            WHERE id = @PlaylistUuid
              AND archived_at IS NULL
              AND (
                  owner_id = @UserUuid
                  OR EXISTS (
                      SELECT 1
                      FROM user_data.playlist_collaborators c
                      WHERE c.playlist_id = playlists.id
                        AND c.user_id = @UserUuid
                        AND c.role = 'editor'
                        AND c.accepted_at IS NOT NULL
                        AND c.revoked_at IS NULL
                  )
              )
            """,
            new { PlaylistUuid = playlistUuid, UserUuid = userUuid },
            transaction) == 1;
    }

    private static async Task<IReadOnlyList<PlaylistEntryRecord>> LoadEntries(
        NpgsqlConnection connection,
        Guid playlistUuid,
        NpgsqlTransaction? transaction)
    {
        return await LoadEntriesForPlaylists(connection, [playlistUuid], transaction);
    }

    private static async Task<IReadOnlyList<PlaylistEntryRecord>> LoadEntriesForPlaylists(
        NpgsqlConnection connection,
        IReadOnlyCollection<Guid> playlistUuids,
        NpgsqlTransaction? transaction)
    {
        var entries = await connection.QueryAsync<PlaylistEntryRecord>(
            """
            SELECT
                id AS "PlaylistEntryUuid",
                playlist_id AS "PlaylistUuid",
                source_track_uuid AS "SourceTrackUuid",
                block_uuid AS "BlockUuid",
                block_position AS "BlockPosition",
                position AS "Position",
                added_by AS "AddedByUserUuid",
                created_at AS "CreatedAt",
                updated_at AS "UpdatedAt"
            FROM user_data.playlist_entries
            WHERE playlist_id = ANY(@PlaylistUuids)
            ORDER BY playlist_id, position
            """,
            new { PlaylistUuids = playlistUuids },
            transaction);

        return entries.ToList();
    }

    private static PlaylistResponse ToResponse(PlaylistSnapshot snapshot)
    {
        return new PlaylistResponse
        {
            PlaylistUuid = snapshot.Playlist.PlaylistUuid,
            ShortId = snapshot.Playlist.ShortId,
            OwnerUserUuid = snapshot.Playlist.OwnerUserUuid,
            Name = snapshot.Playlist.Name,
            Description = snapshot.Playlist.Description,
            Visibility = snapshot.Playlist.Visibility,
            CurrentRevision = snapshot.Playlist.CurrentRevision,
            Entries = snapshot.Entries
                .Select(entry => new PlaylistEntryResponse
                {
                    PlaylistEntryUuid = entry.PlaylistEntryUuid,
                    SourceTrackUuid = entry.SourceTrackUuid,
                    BlockUuid = entry.BlockUuid,
                    BlockPosition = entry.BlockPosition,
                    Position = entry.Position,
                    AddedByUserUuid = entry.AddedByUserUuid
                })
                .ToList()
        };
    }

    private static string NormalizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > MaxPlaylistNameLength)
        {
            throw new PlaylistOperationException("invalid_playlist_name");
        }

        return name.Trim();
    }

    private static string? NormalizeDescription(string? description)
    {
        return string.IsNullOrWhiteSpace(description) ? null : description.Trim();
    }

    private static string PositionForOrdinal(int zeroBasedOrdinal)
    {
        return (zeroBasedOrdinal + 1).ToString("D10");
    }

    private static string TemporaryPosition(Guid entryUuid)
    {
        return $"~new~{entryUuid:N}";
    }

    private static string ReplayStatus(string resultStatus)
    {
        return resultStatus == "applied" ? "noop_already_applied" : resultStatus;
    }

    private sealed class ExistingOperationRow
    {
        public required Guid PlaylistUuid { get; init; }
        public required string OperationJson { get; init; }
        public required long ResultRevision { get; init; }
        public required string ResultStatus { get; init; }
    }

    private sealed class PlaylistMutation
    {
        public required string ResultStatus { get; init; }
        public required IReadOnlyList<PlaylistEntryMutation> FinalEntries { get; init; }
        public IReadOnlyList<PlaylistEntryMutation> NewEntries { get; init; } = [];
        public Guid? NewBlockUuid { get; init; }

        public static PlaylistMutation Applied(
            IReadOnlyList<PlaylistEntryMutation> finalEntries,
            IReadOnlyList<PlaylistEntryMutation> newEntries,
            Guid? newBlockUuid)
        {
            return new PlaylistMutation
            {
                ResultStatus = "applied",
                FinalEntries = finalEntries,
                NewEntries = newEntries,
                NewBlockUuid = newBlockUuid
            };
        }

        public static PlaylistMutation NoStateChange(
            string resultStatus,
            IReadOnlyList<PlaylistEntryMutation> finalEntries)
        {
            return new PlaylistMutation
            {
                ResultStatus = resultStatus,
                FinalEntries = finalEntries
            };
        }
    }

    private sealed class PlaylistEntryMutation
    {
        public required Guid PlaylistEntryUuid { get; init; }
        public required Guid PlaylistUuid { get; init; }
        public required Guid SourceTrackUuid { get; init; }
        public Guid? BlockUuid { get; set; }
        public int? BlockPosition { get; set; }
        public required string Position { get; set; }
        public required Guid AddedByUserUuid { get; init; }
        public required DateTimeOffset CreatedAt { get; init; }
        public required DateTimeOffset UpdatedAt { get; init; }

        public PlaylistEntryRecord ToRecord(string position)
        {
            return new PlaylistEntryRecord
            {
                PlaylistEntryUuid = PlaylistEntryUuid,
                PlaylistUuid = PlaylistUuid,
                SourceTrackUuid = SourceTrackUuid,
                BlockUuid = BlockUuid,
                BlockPosition = BlockPosition,
                Position = position,
                AddedByUserUuid = AddedByUserUuid,
                CreatedAt = CreatedAt,
                UpdatedAt = UpdatedAt
            };
        }
    }

    private static bool JsonOperationsEqual(string left, string right)
    {
        return JToken.DeepEquals(JToken.Parse(left), JToken.Parse(right));
    }
}

public sealed class PlaylistOperationException : Exception
{
    public PlaylistOperationException(string code)
        : base(code)
    {
        Code = code;
    }

    public string Code { get; }
}
