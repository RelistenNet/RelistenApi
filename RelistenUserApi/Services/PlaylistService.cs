using System.Security.Cryptography;
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
    private static readonly char[] ShortIdAlphabet =
        "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz".ToCharArray();

    private readonly UserApiDbService _db;

    public PlaylistService(UserApiDbService db)
    {
        _db = db;
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
        var playlistUuid = request.PlaylistUuid ?? Guid.NewGuid();
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
                        ShortId = GenerateShortId(),
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
                result_revision AS "ResultRevision"
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
                    ResultStatus = "noop_already_applied",
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
        if (entries.Count >= MaxEntriesPerPlaylist)
        {
            throw new PlaylistOperationException("rejected_limit");
        }

        ValidateClientIds(request, entries);

        var newEntries = request.Op switch
        {
            "add_track" => BuildSingleEntry(request, userUuid, playlistUuid, entries.Count),
            "add_tracks_as_block" => BuildBlockEntries(request, userUuid, playlistUuid, entries.Count),
            _ => throw new PlaylistOperationException("unsupported_operation")
        };

        if (entries.Count + newEntries.Count > MaxEntriesPerPlaylist)
        {
            throw new PlaylistOperationException("rejected_limit");
        }

        if (request.Op == "add_tracks_as_block" && request.BlockUuid.HasValue)
        {
            await InsertBlock(
                connection,
                transaction,
                request.BlockUuid.Value,
                playlistUuid,
                userUuid,
                DateTimeOffset.UtcNow);
        }

        foreach (var entry in newEntries)
        {
            await InsertEntry(connection, transaction, entry);
        }

        var resultRevision = playlist.CurrentRevision + 1;
        await connection.ExecuteAsync(
            """
            UPDATE user_data.playlists
            SET current_revision = @ResultRevision,
                updated_at = @Now
            WHERE id = @PlaylistUuid
            """,
            new
            {
                PlaylistUuid = playlistUuid,
                ResultRevision = resultRevision,
                Now = DateTimeOffset.UtcNow
            },
            transaction);

        await connection.ExecuteAsync(
            """
            INSERT INTO user_data.playlist_edit_log
                (id, playlist_id, user_id, operation, idempotency_key, base_revision,
                 result_revision, result_status, created_at)
            VALUES
                (@LogUuid, @PlaylistUuid, @UserUuid, CAST(@OperationJson AS jsonb), @IdempotencyKey,
                 @BaseRevision, @ResultRevision, 'applied', @Now)
            """,
            new
            {
                LogUuid = Guid.NewGuid(),
                PlaylistUuid = playlistUuid,
                UserUuid = userUuid,
                OperationJson = operationJson,
                request.IdempotencyKey,
                request.BaseRevision,
                ResultRevision = resultRevision,
                Now = DateTimeOffset.UtcNow
            },
            transaction);

        var snapshot = await LoadSnapshot(connection, playlistUuid, transaction)
            ?? throw new InvalidOperationException("Updated playlist could not be loaded.");
        await transaction.CommitAsync();

        return new PlaylistOperationResponse
        {
            ResultRevision = resultRevision,
            ResultStatus = "applied",
            Playlist = ToResponse(snapshot)
        };
    }

    private static IReadOnlyList<PlaylistEntryRecord> BuildSingleEntry(
        PlaylistOperationRequest request,
        Guid userUuid,
        Guid playlistUuid,
        int existingCount)
    {
        if (request.EntryUuid == null ||
            request.SourceTrackUuid == null ||
            request.BlockUuid != null ||
            request.EntryUuids != null ||
            request.SourceTrackUuids != null)
        {
            throw new PlaylistOperationException("invalid_operation");
        }

        RejectPlacement(request);

        return
        [
            NewEntry(
                playlistUuid,
                request.EntryUuid.Value,
                request.SourceTrackUuid.Value,
                blockUuid: null,
                blockPosition: null,
                position: PositionForOrdinal(existingCount),
                userUuid)
        ];
    }

    private static IReadOnlyList<PlaylistEntryRecord> BuildBlockEntries(
        PlaylistOperationRequest request,
        Guid userUuid,
        Guid playlistUuid,
        int existingCount)
    {
        if (request.BlockUuid == null ||
            request.EntryUuids is not { Count: > 0 } ||
            request.SourceTrackUuids is not { Count: > 0 } ||
            request.EntryUuids.Count != request.SourceTrackUuids.Count ||
            request.EntryUuid != null ||
            request.SourceTrackUuid != null)
        {
            throw new PlaylistOperationException("invalid_operation");
        }

        RejectPlacement(request);

        return request.EntryUuids
            .Select((entryUuid, index) => NewEntry(
                playlistUuid,
                entryUuid,
                request.SourceTrackUuids[index],
                request.BlockUuid.Value,
                index,
                PositionForOrdinal(existingCount + index),
                userUuid))
            .ToList();
    }

    private static PlaylistEntryRecord NewEntry(
        Guid playlistUuid,
        Guid entryUuid,
        Guid sourceTrackUuid,
        Guid? blockUuid,
        int? blockPosition,
        string position,
        Guid userUuid)
    {
        var now = DateTimeOffset.UtcNow;
        return new PlaylistEntryRecord
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

    private static void ValidateClientIds(
        PlaylistOperationRequest request,
        IReadOnlyList<PlaylistEntryRecord> existingEntries)
    {
        var requestedEntryUuids = request.Op switch
        {
            "add_track" when request.EntryUuid.HasValue => [request.EntryUuid.Value],
            "add_tracks_as_block" when request.EntryUuids != null => request.EntryUuids,
            _ => []
        };

        if (requestedEntryUuids.Count != requestedEntryUuids.Distinct().Count() ||
            requestedEntryUuids.Any(entryUuid =>
                existingEntries.Any(existing => existing.PlaylistEntryUuid == entryUuid)))
        {
            throw new PlaylistOperationException("entry_uuid_conflict");
        }

        if (request.BlockUuid.HasValue &&
            existingEntries.Any(existing => existing.BlockUuid == request.BlockUuid))
        {
            throw new PlaylistOperationException("block_uuid_conflict");
        }
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

    private static void RejectPlacement(PlaylistOperationRequest request)
    {
        if (request.Placement != null)
        {
            throw new PlaylistOperationException("unsupported_placement");
        }
    }

    private static string GenerateShortId()
    {
        Span<byte> bytes = stackalloc byte[10];
        RandomNumberGenerator.Fill(bytes);
        Span<char> chars = stackalloc char[10];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = ShortIdAlphabet[bytes[i] % ShortIdAlphabet.Length];
        }

        return new string(chars);
    }

    private sealed class ExistingOperationRow
    {
        public required Guid PlaylistUuid { get; init; }
        public required string OperationJson { get; init; }
        public required long ResultRevision { get; init; }
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
