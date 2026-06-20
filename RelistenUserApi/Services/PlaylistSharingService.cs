using Dapper;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Npgsql;
using Relisten.UserApi.Models;
using Relisten.UserApi.Serialization;

namespace Relisten.UserApi.Services;

public sealed class PlaylistSharingService
{
    private const int MaxPlaylistNameLength = 200;
    private static readonly TimeSpan MobileGrantLifetime = TimeSpan.FromHours(24);

    private readonly UserApiDbService _db;
    private readonly OpaqueTokenService _tokens;
    private readonly ShortIdService _shortIds;

    public PlaylistSharingService(UserApiDbService db, OpaqueTokenService tokens, ShortIdService shortIds)
    {
        _db = db;
        _tokens = tokens;
        _shortIds = shortIds;
    }

    public async Task<PlaylistShareTokenResponse?> CreateShareToken(
        Guid ownerUserUuid,
        Guid playlistUuid,
        CreatePlaylistShareTokenRequest request)
    {
        var role = NormalizeShareRole(request.Role);
        var now = DateTimeOffset.UtcNow;
        if (request.ExpiresAt.HasValue && request.ExpiresAt.Value <= now)
        {
            throw new PlaylistOperationException("invalid_share_token_expiry");
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await using var connection = _db.CreateConnection();
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            var playlist = await LockPlaylistForOwner(connection, transaction, ownerUserUuid, playlistUuid);
            if (playlist == null)
            {
                await transaction.RollbackAsync();
                return null;
            }

            var issued = _tokens.IssueBearer();
            var record = new PlaylistShareTokenRecord
            {
                ShareTokenUuid = Guid.NewGuid(),
                PlaylistUuid = playlistUuid,
                CreatedByUserUuid = ownerUserUuid,
                Role = role,
                TokenHash = issued.SecretHash,
                ExpiresAt = request.ExpiresAt,
                CreatedAt = now
            };

            try
            {
                await connection.ExecuteAsync(
                    """
                    INSERT INTO user_data.playlist_share_tokens
                        (id, playlist_id, created_by, role, token_hash, expires_at, created_at)
                    VALUES
                        (@ShareTokenUuid, @PlaylistUuid, @CreatedByUserUuid, @Role, @TokenHash,
                         @ExpiresAt, @CreatedAt)
                    """,
                    record,
                    transaction);

                if (playlist.Visibility == "private")
                {
                    await connection.ExecuteAsync(
                        """
                        UPDATE user_data.playlists
                        SET visibility = 'unlisted',
                            current_revision = current_revision + 1,
                            updated_at = @Now,
                            sync_version = nextval('user_data.user_sync_version_seq')
                        WHERE id = @PlaylistUuid
                        """,
                        new { PlaylistUuid = playlistUuid, Now = now },
                        transaction);
                }

                await transaction.CommitAsync();
                return ToResponse(record, issued.Plaintext);
            }
            catch (PostgresException ex) when (
                ex.SqlState == PostgresErrorCodes.UniqueViolation &&
                string.Equals(ex.ConstraintName, "playlist_share_tokens_token_hash_key", StringComparison.Ordinal))
            {
                await transaction.RollbackAsync();
            }
        }

        throw new PlaylistOperationException("share_token_collision");
    }

    public async Task<bool> RevokeShareToken(Guid ownerUserUuid, Guid playlistUuid, Guid shareTokenUuid)
    {
        await using var connection = _db.CreateConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var shareToken = await LockShareTokenForOwner(
            connection,
            transaction,
            ownerUserUuid,
            playlistUuid,
            shareTokenUuid);
        if (shareToken == null)
        {
            await transaction.RollbackAsync();
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        var affected = await connection.ExecuteAsync(
            """
            UPDATE user_data.playlist_share_tokens
            SET revoked_at = COALESCE(revoked_at, @Now)
            WHERE id = @ShareTokenUuid AND playlist_id = @PlaylistUuid
            """,
            new { ShareTokenUuid = shareTokenUuid, PlaylistUuid = playlistUuid, Now = now },
            transaction);

        if (affected == 0)
        {
            await transaction.RollbackAsync();
            return false;
        }

        await connection.ExecuteAsync(
            """
            UPDATE user_data.playlist_mobile_access_grants
            SET revoked_at = COALESCE(revoked_at, @Now)
            WHERE source_share_token_id = @ShareTokenUuid AND revoked_at IS NULL
            """,
            new { ShareTokenUuid = shareTokenUuid, Now = now },
            transaction);

        await transaction.CommitAsync();
        return true;
    }

    public async Task<ExchangePlaylistShareTokenResponse> ExchangeShareToken(
        Guid? userUuid,
        string playlistIdentifier,
        ExchangePlaylistShareTokenRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Token))
        {
            throw new PlaylistOperationException("invalid_share_token");
        }

        var now = DateTimeOffset.UtcNow;
        await using var connection = _db.CreateConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var shareToken = await LoadValidShareToken(
            connection,
            transaction,
            playlistIdentifier,
            _tokens.HashSecret(request.Token),
            now);
        if (shareToken == null)
        {
            throw new PlaylistOperationException("invalid_share_token");
        }

        var playlist = await LoadPlaylistRecord(connection, shareToken.PlaylistUuid, transaction)
            ?? throw new PlaylistOperationException("invalid_share_token");
        if (playlist.Visibility == "private")
        {
            throw new PlaylistOperationException("invalid_share_token");
        }

        if (shareToken.Role == "editor")
        {
            if (!userUuid.HasValue)
            {
                throw new PlaylistOperationException("sign_in_required");
            }

            await UpsertEditorCollaborator(connection, transaction, shareToken.PlaylistUuid, userUuid.Value, now);
            var collaboratorSnapshot = await BuildSnapshot(connection, playlist, transaction);
            await transaction.CommitAsync();

            return new ExchangePlaylistShareTokenResponse
            {
                ResultStatus = "collaborator_access_granted",
                AccessRole = "editor",
                Playlist = ToResponse(collaboratorSnapshot)
            };
        }

        if (string.IsNullOrWhiteSpace(request.DeviceId) || string.IsNullOrWhiteSpace(request.Platform))
        {
            throw new PlaylistOperationException("invalid_device");
        }

        var mobileGrantExpiresAt = MinExpiresAt(now.Add(MobileGrantLifetime), shareToken.ExpiresAt);
        var mobileGrant = _tokens.IssueSelector(now, mobileGrantExpiresAt - now);
        await InsertMobileGrant(
            connection,
            transaction,
            shareToken,
            request.DeviceId.Trim(),
            request.Platform.Trim().ToLowerInvariant(),
            mobileGrant);

        var snapshot = await BuildSnapshot(connection, playlist, transaction);
        await transaction.CommitAsync();

        return new ExchangePlaylistShareTokenResponse
        {
            ResultStatus = "mobile_grant_issued",
            AccessRole = "viewer",
            MobileAccessGrant = new PlaylistMobileAccessGrantResponse
            {
                Token = mobileGrant.Plaintext,
                DeviceId = request.DeviceId.Trim(),
                Platform = request.Platform.Trim().ToLowerInvariant(),
                ExpiresAt = mobileGrant.ExpiresAt
            },
            Playlist = ToResponse(snapshot)
        };
    }

    public async Task<PlaylistAccess?> GetForViewer(
        Guid? userUuid,
        string playlistIdentifier,
        PlaylistMobileGrantCredential? mobileGrant)
    {
        await using var connection = _db.CreateConnection();
        var playlist = await LoadPlaylistRecord(connection, playlistIdentifier, transaction: null);
        if (playlist == null)
        {
            return null;
        }

        return await ResolveAccess(connection, playlist, userUuid, mobileGrant, transaction: null);
    }

    public async Task<PlaylistViewerStateResponse?> GetViewerState(Guid userUuid, Guid playlistUuid)
    {
        await using var connection = _db.CreateConnection();
        var playlist = await LoadPlaylistRecord(connection, playlistUuid, transaction: null);
        if (playlist == null)
        {
            return null;
        }

        var access = await ResolveAccess(connection, playlist, userUuid, mobileGrant: null, transaction: null);
        return access?.ViewerState;
    }

    public async Task<PlaylistViewerStateResponse?> Follow(
        Guid userUuid,
        Guid playlistUuid,
        PlaylistMobileGrantCredential? mobileGrant)
    {
        await using var connection = _db.CreateConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var playlist = await LoadPlaylistRecord(connection, playlistUuid, transaction);
        if (playlist == null)
        {
            await transaction.RollbackAsync();
            return null;
        }

        var access = await ResolveAccess(connection, playlist, userUuid, mobileGrant, transaction);
        if (access == null)
        {
            await transaction.RollbackAsync();
            return null;
        }

        if (!access.ViewerState.IsOwner)
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO user_data.playlist_followers
                    (playlist_id, user_id, followed_at)
                VALUES
                    (@PlaylistUuid, @UserUuid, @Now)
                ON CONFLICT (playlist_id, user_id)
                DO UPDATE SET
                    followed_at = CASE
                        WHEN user_data.playlist_followers.unfollowed_at IS NULL
                        THEN user_data.playlist_followers.followed_at
                        ELSE @Now
                    END,
                    unfollowed_at = NULL,
                    sync_version = CASE
                        WHEN user_data.playlist_followers.unfollowed_at IS NULL
                        THEN user_data.playlist_followers.sync_version
                        ELSE nextval('user_data.user_sync_version_seq')
                    END
                """,
                new { PlaylistUuid = playlistUuid, UserUuid = userUuid, Now = DateTimeOffset.UtcNow },
                transaction);
        }

        await transaction.CommitAsync();
        return await GetViewerState(userUuid, playlistUuid)
            ?? throw new InvalidOperationException("Followed playlist could not be loaded.");
    }

    public async Task<PlaylistResponse?> UpdateVisibility(
        Guid ownerUserUuid,
        Guid playlistUuid,
        UpdatePlaylistVisibilityRequest request)
    {
        var visibility = NormalizeVisibility(request.Visibility);
        await using var connection = _db.CreateConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var playlist = await LockPlaylistForOwner(connection, transaction, ownerUserUuid, playlistUuid);
        if (playlist == null)
        {
            await transaction.RollbackAsync();
            return null;
        }

        if (playlist.Visibility != visibility)
        {
            await connection.ExecuteAsync(
                """
                UPDATE user_data.playlists
                SET visibility = @Visibility,
                    current_revision = current_revision + 1,
                    updated_at = @Now,
                    sync_version = nextval('user_data.user_sync_version_seq')
                WHERE id = @PlaylistUuid
                """,
                new
                {
                    PlaylistUuid = playlistUuid,
                    Visibility = visibility,
                    Now = DateTimeOffset.UtcNow
                },
                transaction);
        }

        var updated = await LoadPlaylistRecord(connection, playlistUuid, transaction)
            ?? throw new InvalidOperationException("Updated playlist could not be loaded.");
        var snapshot = await BuildSnapshot(connection, updated, transaction);
        await transaction.CommitAsync();
        return ToResponse(snapshot);
    }

    public async Task<PlaylistCloneResult?> ClonePlaylist(
        Guid userUuid,
        string playlistIdentifier,
        PlaylistMobileGrantCredential? mobileGrant,
        ClonePlaylistRequest request)
    {
        if (request.IdempotencyKey == Guid.Empty || request.NewPlaylistUuid == Guid.Empty)
        {
            throw new PlaylistOperationException("invalid_idempotency_key");
        }

        await using var connection = _db.CreateConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await LockIdempotencyKey(connection, transaction, request.IdempotencyKey);
        var sourcePlaylist = await LockPlaylistForClone(connection, playlistIdentifier, transaction);
        if (sourcePlaylist == null)
        {
            await transaction.RollbackAsync();
            return null;
        }
        var sourceSnapshot = await BuildSnapshot(connection, sourcePlaylist, transaction);

        var existing = await LoadExistingOperation(connection, transaction, request.IdempotencyKey);
        if (existing != null)
        {
            if (existing.PlaylistUuid != request.NewPlaylistUuid ||
                !CloneOperationsMatch(existing.OperationJson, request, sourcePlaylist.PlaylistUuid))
            {
                throw new PlaylistOperationException("idempotency_key_conflict");
            }

            var replay = await LoadPlaylistRecord(connection, request.NewPlaylistUuid, transaction);
            if (replay == null || replay.OwnerUserUuid != userUuid)
            {
                throw new PlaylistOperationException("idempotency_key_conflict");
            }

            var replaySnapshot = await BuildSnapshot(connection, replay, transaction);
            await transaction.CommitAsync();
            return new PlaylistCloneResult(ToResponse(replaySnapshot), Created: false);
        }

        var sourceAccess = await ResolveAccess(connection, sourcePlaylist, userUuid, mobileGrant, transaction);
        if (sourceAccess == null)
        {
            await transaction.RollbackAsync();
            return null;
        }

        var cloneRecord = await InsertClonedPlaylist(
            connection,
            transaction,
            userUuid,
            sourceSnapshot,
            request);
        await InsertCloneOperationLog(
            connection,
            transaction,
            cloneRecord.PlaylistUuid,
            sourcePlaylist.PlaylistUuid,
            userUuid,
            request);
        var clonedSnapshot = await BuildSnapshot(connection, cloneRecord, transaction);
        await transaction.CommitAsync();

        return new PlaylistCloneResult(ToResponse(clonedSnapshot), Created: true);
    }

    public async Task<PlaylistCollaboratorResponse?> InviteCollaborator(
        Guid ownerUserUuid,
        Guid playlistUuid,
        CreatePlaylistCollaboratorInvitationRequest request)
    {
        var username = NormalizeUsername(request.Username);
        await using var connection = _db.CreateConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var playlist = await LockPlaylistForOwner(connection, transaction, ownerUserUuid, playlistUuid);
        if (playlist == null)
        {
            await transaction.RollbackAsync();
            return null;
        }

        var invitee = await LoadUserByUsername(connection, username, transaction);
        if (invitee == null)
        {
            throw new PlaylistOperationException("user_not_found");
        }

        if (invitee.UserUuid == ownerUserUuid)
        {
            throw new PlaylistOperationException("invalid_collaborator");
        }

        var now = DateTimeOffset.UtcNow;
        await connection.ExecuteAsync(
            """
            INSERT INTO user_data.playlist_collaborators
                (id, playlist_id, user_id, role, invited_by, invited_at)
            VALUES
                (@CollaboratorUuid, @PlaylistUuid, @UserUuid, 'editor', @OwnerUserUuid, @Now)
            ON CONFLICT (playlist_id, user_id)
            DO UPDATE SET
                role = 'editor',
                invited_by = @OwnerUserUuid,
                invited_at = @Now,
                accepted_at = CASE
                    WHEN user_data.playlist_collaborators.revoked_at IS NULL
                    THEN user_data.playlist_collaborators.accepted_at
                    ELSE NULL
                END,
                revoked_at = NULL,
                sync_version = nextval('user_data.user_sync_version_seq')
            """,
            new
            {
                CollaboratorUuid = Guid.NewGuid(),
                PlaylistUuid = playlistUuid,
                invitee.UserUuid,
                OwnerUserUuid = ownerUserUuid,
                Now = now
            },
            transaction);

        var collaborator = await LoadCollaborator(connection, playlistUuid, invitee.UserUuid, transaction)
            ?? throw new InvalidOperationException("Invited collaborator could not be loaded.");
        await transaction.CommitAsync();
        return collaborator;
    }

    public async Task<PlaylistCollaboratorResponse?> AcceptCollaboratorInvitation(Guid userUuid, Guid playlistUuid)
    {
        await using var connection = _db.CreateConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var playlistExists = await LoadPlaylistRecord(connection, playlistUuid, transaction) != null;
        if (!playlistExists)
        {
            await transaction.RollbackAsync();
            return null;
        }

        var collaborator = await LockCollaborator(connection, playlistUuid, userUuid, transaction);
        if (collaborator == null || collaborator.RevokedAt.HasValue)
        {
            await transaction.RollbackAsync();
            return null;
        }

        if (!collaborator.AcceptedAt.HasValue)
        {
            await connection.ExecuteAsync(
                """
                UPDATE user_data.playlist_collaborators
                SET accepted_at = @Now,
                    sync_version = nextval('user_data.user_sync_version_seq')
                WHERE playlist_id = @PlaylistUuid
                  AND user_id = @UserUuid
                  AND revoked_at IS NULL
                """,
                new { PlaylistUuid = playlistUuid, UserUuid = userUuid, Now = DateTimeOffset.UtcNow },
                transaction);
        }

        var accepted = await LoadCollaborator(connection, playlistUuid, userUuid, transaction)
            ?? throw new InvalidOperationException("Accepted collaborator could not be loaded.");
        await transaction.CommitAsync();
        return accepted;
    }

    public async Task<bool> RevokeCollaborator(Guid ownerUserUuid, Guid playlistUuid, Guid collaboratorUserUuid)
    {
        if (ownerUserUuid == collaboratorUserUuid)
        {
            return false;
        }

        await using var connection = _db.CreateConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var playlist = await LockPlaylistForOwner(connection, transaction, ownerUserUuid, playlistUuid);
        if (playlist == null)
        {
            await transaction.RollbackAsync();
            return false;
        }

        var affected = await connection.ExecuteAsync(
            """
            UPDATE user_data.playlist_collaborators
            SET revoked_at = COALESCE(revoked_at, @Now),
                sync_version = CASE
                    WHEN revoked_at IS NULL
                    THEN nextval('user_data.user_sync_version_seq')
                    ELSE sync_version
                END
            WHERE playlist_id = @PlaylistUuid
              AND user_id = @CollaboratorUserUuid
            """,
            new
            {
                PlaylistUuid = playlistUuid,
                CollaboratorUserUuid = collaboratorUserUuid,
                Now = DateTimeOffset.UtcNow
            },
            transaction);

        if (affected == 0)
        {
            await transaction.RollbackAsync();
            return false;
        }

        await transaction.CommitAsync();
        return true;
    }

    private async Task<PlaylistRecord> InsertClonedPlaylist(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid ownerUserUuid,
        PlaylistSnapshot sourceSnapshot,
        ClonePlaylistRequest request)
    {
        var now = DateTimeOffset.UtcNow;
        var name = CloneName(sourceSnapshot.Playlist.Name, request.Name);
        var description = CloneDescription(sourceSnapshot.Playlist.Description, request.Description);
        PlaylistRecord? clone = null;

        for (var attempt = 0; attempt < 5; attempt++)
        {
            clone = new PlaylistRecord
            {
                PlaylistUuid = request.NewPlaylistUuid,
                ShortId = _shortIds.Generate(),
                OwnerUserUuid = ownerUserUuid,
                Name = name,
                Description = description,
                Visibility = "private",
                CurrentRevision = 1,
                CreatedAt = now,
                UpdatedAt = now
            };

            await connection.ExecuteAsync("SAVEPOINT clone_playlist_short_id", transaction: transaction);
            try
            {
                await connection.ExecuteAsync(
                    """
                    INSERT INTO user_data.playlists
                        (id, short_id, owner_id, name, description, visibility, current_revision,
                         moderation_status, created_at, updated_at)
                    VALUES
                        (@PlaylistUuid, @ShortId, @OwnerUserUuid, @Name, @Description, 'private', 1,
                         'approved', @CreatedAt, @UpdatedAt)
                    """,
                    clone,
                    transaction);
                await connection.ExecuteAsync("RELEASE SAVEPOINT clone_playlist_short_id", transaction: transaction);
                break;
            }
            catch (PostgresException ex) when (
                ex.SqlState == PostgresErrorCodes.UniqueViolation &&
                string.Equals(ex.ConstraintName, "playlists_pkey", StringComparison.Ordinal))
            {
                await RollbackClonePlaylistSavepoint(connection, transaction);
                throw new PlaylistOperationException("playlist_uuid_conflict");
            }
            catch (PostgresException ex) when (
                ex.SqlState == PostgresErrorCodes.UniqueViolation &&
                string.Equals(ex.ConstraintName, "playlists_short_id_key", StringComparison.Ordinal))
            {
                await RollbackClonePlaylistSavepoint(connection, transaction);
                if (attempt == 4)
                {
                    throw new PlaylistOperationException("short_id_collision");
                }
            }
        }

        if (clone == null)
        {
            throw new PlaylistOperationException("short_id_collision");
        }

        var blockUuidMap = sourceSnapshot.Entries
            .Where(entry => entry.BlockUuid.HasValue)
            .Select(entry => entry.BlockUuid!.Value)
            .Distinct()
            .ToDictionary(blockUuid => blockUuid, _ => Guid.NewGuid());

        foreach (var block in blockUuidMap)
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO user_data.playlist_blocks
                    (id, playlist_id, created_by, created_at)
                VALUES
                    (@BlockUuid, @PlaylistUuid, @OwnerUserUuid, @Now)
                """,
                new
                {
                    BlockUuid = block.Value,
                    clone.PlaylistUuid,
                    OwnerUserUuid = ownerUserUuid,
                    Now = now
                },
                transaction);
        }

        foreach (var entry in sourceSnapshot.Entries)
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO user_data.playlist_entries
                    (id, playlist_id, source_track_uuid, block_uuid, position, block_position,
                     added_by, created_at, updated_at)
                VALUES
                    (@PlaylistEntryUuid, @PlaylistUuid, @SourceTrackUuid, @BlockUuid, @Position,
                     @BlockPosition, @OwnerUserUuid, @Now, @Now)
                """,
                new
                {
                    PlaylistEntryUuid = Guid.NewGuid(),
                    clone.PlaylistUuid,
                    entry.SourceTrackUuid,
                    BlockUuid = entry.BlockUuid.HasValue ? blockUuidMap[entry.BlockUuid.Value] : (Guid?)null,
                    entry.Position,
                    entry.BlockPosition,
                    OwnerUserUuid = ownerUserUuid,
                    Now = now
                },
                transaction);
        }

        return clone;
    }

    private static async Task RollbackClonePlaylistSavepoint(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        await connection.ExecuteAsync("ROLLBACK TO SAVEPOINT clone_playlist_short_id", transaction: transaction);
        await connection.ExecuteAsync("RELEASE SAVEPOINT clone_playlist_short_id", transaction: transaction);
    }

    private static Task InsertCloneOperationLog(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid clonePlaylistUuid,
        Guid sourcePlaylistUuid,
        Guid userUuid,
        ClonePlaylistRequest request)
    {
        return connection.ExecuteAsync(
            """
            INSERT INTO user_data.playlist_edit_log
                (id, playlist_id, user_id, operation, idempotency_key, base_revision,
                 result_revision, result_status, created_at)
            VALUES
                (@LogUuid, @ClonePlaylistUuid, @UserUuid, CAST(@OperationJson AS jsonb),
                 @IdempotencyKey, NULL, 1, 'applied', @Now)
            """,
            new
            {
                LogUuid = Guid.NewGuid(),
                ClonePlaylistUuid = clonePlaylistUuid,
                SourcePlaylistUuid = sourcePlaylistUuid,
                UserUuid = userUuid,
                OperationJson = CloneOperationJson(sourcePlaylistUuid, request),
                request.IdempotencyKey,
                Now = DateTimeOffset.UtcNow
            },
            transaction);
    }

    private static string CloneOperationJson(Guid sourcePlaylistUuid, ClonePlaylistRequest request)
    {
        return JsonConvert.SerializeObject(
            new CloneOperationLog
            {
                Op = "clone_playlist",
                SourcePlaylistUuid = sourcePlaylistUuid,
                NewPlaylistUuid = request.NewPlaylistUuid,
                Name = string.IsNullOrWhiteSpace(request.Name) ? null : request.Name.Trim(),
                Description = request.Description == null ? null : request.Description.Trim()
            },
            UserLibraryJson.SerializerSettings);
    }

    private static bool CloneOperationsMatch(
        string existingOperationJson,
        ClonePlaylistRequest request,
        Guid sourcePlaylistUuid)
    {
        return JToken.DeepEquals(
            JToken.Parse(existingOperationJson),
            JToken.Parse(CloneOperationJson(sourcePlaylistUuid, request)));
    }

    private static Task<ExistingOperationRow?> LoadExistingOperation(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid idempotencyKey)
    {
        return connection.QuerySingleOrDefaultAsync<ExistingOperationRow>(
            """
            SELECT
                playlist_id AS "PlaylistUuid",
                operation::text AS "OperationJson"
            FROM user_data.playlist_edit_log
            WHERE idempotency_key = @IdempotencyKey
            """,
            new { IdempotencyKey = idempotencyKey },
            transaction);
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

    private static async Task<UserAccount?> LoadUserByUsername(
        NpgsqlConnection connection,
        string username,
        NpgsqlTransaction transaction)
    {
        return await connection.QuerySingleOrDefaultAsync<UserAccount>(
            """
            SELECT
                id AS "UserUuid",
                username AS "Username",
                display_name AS "DisplayName",
                created_at AS "CreatedAt",
                updated_at AS "UpdatedAt"
            FROM user_data.users
            WHERE username_lower = @UsernameLower
            """,
            new { UsernameLower = username.ToLowerInvariant() },
            transaction);
    }

    private static Task<PlaylistCollaboratorResponse?> LoadCollaborator(
        NpgsqlConnection connection,
        Guid playlistUuid,
        Guid userUuid,
        NpgsqlTransaction transaction)
    {
        return connection.QuerySingleOrDefaultAsync<PlaylistCollaboratorResponse>(
            """
            SELECT
                c.playlist_id AS "PlaylistUuid",
                c.user_id AS "UserUuid",
                u.username AS "Username",
                c.role AS "Role",
                c.invited_by AS "InvitedByUserUuid",
                c.invited_at AS "InvitedAt",
                c.accepted_at AS "AcceptedAt",
                c.revoked_at AS "RevokedAt"
            FROM user_data.playlist_collaborators c
            INNER JOIN user_data.users u ON u.id = c.user_id
            WHERE c.playlist_id = @PlaylistUuid
              AND c.user_id = @UserUuid
            """,
            new { PlaylistUuid = playlistUuid, UserUuid = userUuid },
            transaction);
    }

    private static Task<PlaylistCollaboratorResponse?> LockCollaborator(
        NpgsqlConnection connection,
        Guid playlistUuid,
        Guid userUuid,
        NpgsqlTransaction transaction)
    {
        return connection.QuerySingleOrDefaultAsync<PlaylistCollaboratorResponse>(
            """
            SELECT
                c.playlist_id AS "PlaylistUuid",
                c.user_id AS "UserUuid",
                u.username AS "Username",
                c.role AS "Role",
                c.invited_by AS "InvitedByUserUuid",
                c.invited_at AS "InvitedAt",
                c.accepted_at AS "AcceptedAt",
                c.revoked_at AS "RevokedAt"
            FROM user_data.playlist_collaborators c
            INNER JOIN user_data.users u ON u.id = c.user_id
            WHERE c.playlist_id = @PlaylistUuid
              AND c.user_id = @UserUuid
            FOR UPDATE OF c
            """,
            new { PlaylistUuid = playlistUuid, UserUuid = userUuid },
            transaction);
    }

    private async Task<PlaylistAccess?> ResolveAccess(
        NpgsqlConnection connection,
        PlaylistRecord playlist,
        Guid? userUuid,
        PlaylistMobileGrantCredential? mobileGrant,
        NpgsqlTransaction? transaction)
    {
        var state = new PlaylistViewerStateResponse
        {
            IsOwner = false,
            IsFollowing = false,
            IsCollaborator = false,
            CanEdit = false,
            AccessRole = "none"
        };

        if (userUuid.HasValue)
        {
            if (playlist.OwnerUserUuid == userUuid.Value)
            {
                state = state.WithAccess(isOwner: true, canEdit: true, accessRole: "owner");
                return await BuildAccess(connection, playlist, state, transaction);
            }

            var collaboratorRole = await LoadCollaboratorRole(connection, playlist.PlaylistUuid, userUuid.Value, transaction);
            if (collaboratorRole == "editor")
            {
                state = state.WithAccess(isCollaborator: true, canEdit: true, accessRole: "editor");
                return await BuildAccess(connection, playlist, state, transaction);
            }

            if (await IsFollower(connection, playlist.PlaylistUuid, userUuid.Value, transaction))
            {
                state = state.WithAccess(isFollowing: true, accessRole: "viewer");
                return await BuildAccess(connection, playlist, state, transaction);
            }
        }

        if (playlist.Visibility == "public")
        {
            state = state.WithAccess(accessRole: "viewer");
            return await BuildAccess(connection, playlist, state, transaction);
        }

        if (playlist.Visibility == "unlisted" &&
            mobileGrant != null &&
            await IsValidMobileGrant(connection, playlist.PlaylistUuid, mobileGrant, transaction))
        {
            state = state.WithAccess(accessRole: "viewer");
            return await BuildAccess(connection, playlist, state, transaction);
        }

        return null;
    }

    private static async Task<PlaylistAccess> BuildAccess(
        NpgsqlConnection connection,
        PlaylistRecord playlist,
        PlaylistViewerStateResponse state,
        NpgsqlTransaction? transaction)
    {
        return new PlaylistAccess
        {
            Snapshot = await BuildSnapshot(connection, playlist, transaction),
            ViewerState = state
        };
    }

    private static async Task<PlaylistSnapshot> BuildSnapshot(
        NpgsqlConnection connection,
        PlaylistRecord playlist,
        NpgsqlTransaction? transaction)
    {
        return new PlaylistSnapshot
        {
            Playlist = playlist,
            Entries = await LoadEntries(connection, playlist.PlaylistUuid, transaction)
        };
    }

    private static async Task<PlaylistShareTokenRecord?> LoadValidShareToken(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string playlistIdentifier,
        string tokenHash,
        DateTimeOffset now)
    {
        return await connection.QuerySingleOrDefaultAsync<PlaylistShareTokenRecord>(
            """
            SELECT
                t.id AS "ShareTokenUuid",
                t.playlist_id AS "PlaylistUuid",
                t.created_by AS "CreatedByUserUuid",
                t.role AS "Role",
                t.token_hash AS "TokenHash",
                t.expires_at AS "ExpiresAt",
                t.revoked_at AS "RevokedAt",
                t.created_at AS "CreatedAt"
            FROM user_data.playlist_share_tokens t
            INNER JOIN user_data.playlists p ON p.id = t.playlist_id
            WHERE t.token_hash = @TokenHash
              AND p.archived_at IS NULL
              AND (@PlaylistUuid IS NULL OR p.id = @PlaylistUuid)
              AND (@PlaylistUuid IS NOT NULL OR p.short_id = @PlaylistIdentifier)
              AND t.revoked_at IS NULL
              AND (t.expires_at IS NULL OR t.expires_at > @Now)
            FOR UPDATE OF t
            """,
            new
            {
                TokenHash = tokenHash,
                PlaylistUuid = TryParseGuid(playlistIdentifier),
                PlaylistIdentifier = playlistIdentifier,
                Now = now
            },
            transaction);
    }

    private static Task<PlaylistShareTokenRecord?> LockShareTokenForOwner(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid ownerUserUuid,
        Guid playlistUuid,
        Guid shareTokenUuid)
    {
        return connection.QuerySingleOrDefaultAsync<PlaylistShareTokenRecord>(
            """
            SELECT
                t.id AS "ShareTokenUuid",
                t.playlist_id AS "PlaylistUuid",
                t.created_by AS "CreatedByUserUuid",
                t.role AS "Role",
                t.token_hash AS "TokenHash",
                t.expires_at AS "ExpiresAt",
                t.revoked_at AS "RevokedAt",
                t.created_at AS "CreatedAt"
            FROM user_data.playlist_share_tokens t
            INNER JOIN user_data.playlists p ON p.id = t.playlist_id
            WHERE t.id = @ShareTokenUuid
              AND t.playlist_id = @PlaylistUuid
              AND p.owner_id = @OwnerUserUuid
              AND p.archived_at IS NULL
            FOR UPDATE OF t
            """,
            new
            {
                ShareTokenUuid = shareTokenUuid,
                PlaylistUuid = playlistUuid,
                OwnerUserUuid = ownerUserUuid
            },
            transaction);
    }

    private static async Task InsertMobileGrant(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PlaylistShareTokenRecord shareToken,
        string deviceId,
        string platform,
        OpaqueSelectorToken mobileGrant)
    {
        await connection.ExecuteAsync(
            """
            INSERT INTO user_data.playlist_mobile_access_grants
                (id, playlist_id, source_share_token_id, device_id, platform, role,
                 token_selector, token_secret_hash, issued_at, expires_at)
            VALUES
                (@MobileAccessGrantUuid, @PlaylistUuid, @ShareTokenUuid, @DeviceId, @Platform, @Role,
                 @TokenSelector, @TokenSecretHash, @IssuedAt, @ExpiresAt)
            """,
            new
            {
                MobileAccessGrantUuid = Guid.NewGuid(),
                shareToken.PlaylistUuid,
                shareToken.ShareTokenUuid,
                DeviceId = deviceId,
                Platform = platform,
                shareToken.Role,
                TokenSelector = mobileGrant.Selector,
                TokenSecretHash = mobileGrant.SecretHash,
                mobileGrant.IssuedAt,
                mobileGrant.ExpiresAt
            },
            transaction);
    }

    private static async Task UpsertEditorCollaborator(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid playlistUuid,
        Guid userUuid,
        DateTimeOffset now)
    {
        await connection.ExecuteAsync(
            """
            INSERT INTO user_data.playlist_collaborators
                (id, playlist_id, user_id, role, invited_at, accepted_at)
            VALUES
                (@CollaboratorUuid, @PlaylistUuid, @UserUuid, 'editor', @Now, @Now)
            ON CONFLICT (playlist_id, user_id)
            DO UPDATE SET
                role = 'editor',
                accepted_at = CASE
                    WHEN user_data.playlist_collaborators.revoked_at IS NULL
                     AND user_data.playlist_collaborators.accepted_at IS NOT NULL
                    THEN user_data.playlist_collaborators.accepted_at
                    ELSE @Now
                END,
                revoked_at = NULL,
                sync_version = CASE
                    WHEN user_data.playlist_collaborators.revoked_at IS NULL
                     AND user_data.playlist_collaborators.accepted_at IS NOT NULL
                     AND user_data.playlist_collaborators.role = 'editor'
                    THEN user_data.playlist_collaborators.sync_version
                    ELSE nextval('user_data.user_sync_version_seq')
                END
            """,
            new
            {
                CollaboratorUuid = Guid.NewGuid(),
                PlaylistUuid = playlistUuid,
                UserUuid = userUuid,
                Now = now
            },
            transaction);
    }

    private async Task<bool> IsValidMobileGrant(
        NpgsqlConnection connection,
        Guid playlistUuid,
        PlaylistMobileGrantCredential grant,
        NpgsqlTransaction? transaction)
    {
        var parsed = TryParseSelectorGrant(grant.Token);
        if (parsed == null)
        {
            return false;
        }

        var row = await connection.QuerySingleOrDefaultAsync<PlaylistMobileAccessGrantRecord>(
            """
            SELECT
                id AS "MobileAccessGrantUuid",
                playlist_id AS "PlaylistUuid",
                source_share_token_id AS "SourceShareTokenUuid",
                device_id AS "DeviceId",
                platform AS "Platform",
                role AS "Role",
                token_selector AS "TokenSelector",
                token_secret_hash AS "TokenSecretHash",
                issued_at AS "IssuedAt",
                expires_at AS "ExpiresAt",
                revoked_at AS "RevokedAt"
            FROM user_data.playlist_mobile_access_grants
            WHERE playlist_id = @PlaylistUuid
              AND token_selector = @Selector
              AND device_id = @DeviceId
              AND role = 'viewer'
              AND revoked_at IS NULL
              AND expires_at > @Now
            """,
            new
            {
                PlaylistUuid = playlistUuid,
                parsed.Selector,
                grant.DeviceId,
                Now = DateTimeOffset.UtcNow
            },
            transaction);

        return row != null && _tokens.Verify(parsed.Secret, row.TokenSecretHash);
    }

    private static ParsedSelectorToken? TryParseSelectorGrant(string token)
    {
        var parts = token.Split('.', 2);
        return parts.Length == 2 &&
               !string.IsNullOrWhiteSpace(parts[0]) &&
               !string.IsNullOrWhiteSpace(parts[1])
            ? new ParsedSelectorToken(parts[0], parts[1])
            : null;
    }

    private static async Task<string?> LoadCollaboratorRole(
        NpgsqlConnection connection,
        Guid playlistUuid,
        Guid userUuid,
        NpgsqlTransaction? transaction)
    {
        return await connection.QuerySingleOrDefaultAsync<string?>(
            """
            SELECT role
            FROM user_data.playlist_collaborators
            WHERE playlist_id = @PlaylistUuid
              AND user_id = @UserUuid
              AND accepted_at IS NOT NULL
              AND revoked_at IS NULL
            """,
            new { PlaylistUuid = playlistUuid, UserUuid = userUuid },
            transaction);
    }

    private static async Task<bool> IsFollower(
        NpgsqlConnection connection,
        Guid playlistUuid,
        Guid userUuid,
        NpgsqlTransaction? transaction)
    {
        return await connection.QuerySingleOrDefaultAsync<int?>(
            """
            SELECT 1
            FROM user_data.playlist_followers
            WHERE playlist_id = @PlaylistUuid
              AND user_id = @UserUuid
              AND unfollowed_at IS NULL
            """,
            new { PlaylistUuid = playlistUuid, UserUuid = userUuid },
            transaction) == 1;
    }

    private static Task<PlaylistRecord?> LockPlaylistForOwner(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid ownerUserUuid,
        Guid playlistUuid)
    {
        return connection.QuerySingleOrDefaultAsync<PlaylistRecord>(
            PlaylistSelectSql + """

            WHERE id = @PlaylistUuid AND owner_id = @OwnerUserUuid AND archived_at IS NULL
            FOR UPDATE
            """,
            new { PlaylistUuid = playlistUuid, OwnerUserUuid = ownerUserUuid },
            transaction);
    }

    private static Task<PlaylistRecord?> LoadPlaylistRecord(
        NpgsqlConnection connection,
        Guid playlistUuid,
        NpgsqlTransaction? transaction)
    {
        return connection.QuerySingleOrDefaultAsync<PlaylistRecord>(
            PlaylistSelectSql + """

            WHERE id = @PlaylistUuid AND archived_at IS NULL
            """,
            new { PlaylistUuid = playlistUuid },
            transaction);
    }

    private static Task<PlaylistRecord?> LoadPlaylistRecord(
        NpgsqlConnection connection,
        string playlistIdentifier,
        NpgsqlTransaction? transaction)
    {
        return connection.QuerySingleOrDefaultAsync<PlaylistRecord>(
            PlaylistSelectSql + """

            WHERE archived_at IS NULL
              AND (
                  (@PlaylistUuid IS NOT NULL AND id = @PlaylistUuid)
                  OR
                  (@PlaylistUuid IS NULL AND short_id = @PlaylistIdentifier)
              )
            """,
            new
            {
                PlaylistUuid = TryParseGuid(playlistIdentifier),
                PlaylistIdentifier = playlistIdentifier
            },
            transaction);
    }

    private static Task<PlaylistRecord?> LockPlaylistForClone(
        NpgsqlConnection connection,
        string playlistIdentifier,
        NpgsqlTransaction transaction)
    {
        return connection.QuerySingleOrDefaultAsync<PlaylistRecord>(
            PlaylistSelectSql + """

            WHERE archived_at IS NULL
              AND (
                  (@PlaylistUuid IS NOT NULL AND id = @PlaylistUuid)
                  OR
                  (@PlaylistUuid IS NULL AND short_id = @PlaylistIdentifier)
              )
            FOR SHARE
            """,
            new
            {
                PlaylistUuid = TryParseGuid(playlistIdentifier),
                PlaylistIdentifier = playlistIdentifier
            },
            transaction);
    }

    private static async Task<IReadOnlyList<PlaylistEntryRecord>> LoadEntries(
        NpgsqlConnection connection,
        Guid playlistUuid,
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
            WHERE playlist_id = @PlaylistUuid
            ORDER BY position
            """,
            new { PlaylistUuid = playlistUuid },
            transaction);

        return entries.ToList();
    }

    public static PlaylistResponse ToResponse(PlaylistSnapshot snapshot)
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

    private static PlaylistShareTokenResponse ToResponse(PlaylistShareTokenRecord record, string? token)
    {
        return new PlaylistShareTokenResponse
        {
            ShareTokenUuid = record.ShareTokenUuid,
            PlaylistUuid = record.PlaylistUuid,
            Role = record.Role,
            Token = token,
            ExpiresAt = record.ExpiresAt,
            RevokedAt = record.RevokedAt,
            CreatedAt = record.CreatedAt
        };
    }

    private static string NormalizeShareRole(string role)
    {
        if (string.IsNullOrWhiteSpace(role))
        {
            throw new PlaylistOperationException("invalid_share_role");
        }

        var normalized = role.Trim().ToLowerInvariant();
        return normalized is "viewer" or "editor"
            ? normalized
            : throw new PlaylistOperationException("invalid_share_role");
    }

    private static string NormalizeVisibility(string visibility)
    {
        if (string.IsNullOrWhiteSpace(visibility))
        {
            throw new PlaylistOperationException("invalid_visibility");
        }

        var normalized = visibility.Trim().ToLowerInvariant();
        return normalized is "private" or "unlisted" or "public"
            ? normalized
            : throw new PlaylistOperationException("invalid_visibility");
    }

    private static string NormalizeUsername(string username)
    {
        var normalized = username.Trim();
        return string.IsNullOrWhiteSpace(normalized) || normalized.Length > 30
            ? throw new PlaylistOperationException("invalid_username")
            : normalized;
    }

    private static string CloneName(string sourceName, string? requestedName)
    {
        var name = string.IsNullOrWhiteSpace(requestedName)
            ? DefaultCloneName(sourceName)
            : requestedName.Trim();
        if (name.Length > MaxPlaylistNameLength)
        {
            throw new PlaylistOperationException("invalid_playlist_name");
        }

        return name;
    }

    private static string DefaultCloneName(string sourceName)
    {
        const string suffix = " Copy";
        return sourceName.Length + suffix.Length <= MaxPlaylistNameLength
            ? sourceName + suffix
            : sourceName[..MaxPlaylistNameLength];
    }

    private static string? CloneDescription(string? sourceDescription, string? requestedDescription)
    {
        return requestedDescription == null
            ? sourceDescription
            : string.IsNullOrWhiteSpace(requestedDescription) ? null : requestedDescription.Trim();
    }

    private static Guid? TryParseGuid(string value)
    {
        return Guid.TryParse(value, out var guid) ? guid : null;
    }

    private static DateTimeOffset MinExpiresAt(DateTimeOffset defaultExpiresAt, DateTimeOffset? cappedExpiresAt)
    {
        return cappedExpiresAt.HasValue && cappedExpiresAt.Value < defaultExpiresAt
            ? cappedExpiresAt.Value
            : defaultExpiresAt;
    }

    private sealed class ExistingOperationRow
    {
        public required Guid PlaylistUuid { get; init; }
        public required string OperationJson { get; init; }
    }

    private sealed class CloneOperationLog
    {
        public required string Op { get; init; }
        public required Guid SourcePlaylistUuid { get; init; }
        public required Guid NewPlaylistUuid { get; init; }
        public string? Name { get; init; }
        public string? Description { get; init; }
    }

    private const string PlaylistSelectSql = """
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
        """;
}

public sealed record PlaylistCloneResult(PlaylistResponse Playlist, bool Created);

internal static class PlaylistViewerStateExtensions
{
    public static PlaylistViewerStateResponse WithAccess(
        this PlaylistViewerStateResponse state,
        bool? isOwner = null,
        bool? isFollowing = null,
        bool? isCollaborator = null,
        bool? canEdit = null,
        string? accessRole = null)
    {
        return new PlaylistViewerStateResponse
        {
            IsOwner = isOwner ?? state.IsOwner,
            IsFollowing = isFollowing ?? state.IsFollowing,
            IsCollaborator = isCollaborator ?? state.IsCollaborator,
            CanEdit = canEdit ?? state.CanEdit,
            AccessRole = accessRole ?? state.AccessRole
        };
    }
}
