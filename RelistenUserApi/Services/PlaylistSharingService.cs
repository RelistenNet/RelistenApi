using Dapper;
using Npgsql;
using Relisten.UserApi.Models;

namespace Relisten.UserApi.Services;

public sealed class PlaylistSharingService
{
    private static readonly TimeSpan MobileGrantLifetime = TimeSpan.FromHours(24);

    private readonly UserApiDbService _db;
    private readonly OpaqueTokenService _tokens;

    public PlaylistSharingService(UserApiDbService db, OpaqueTokenService tokens)
    {
        _db = db;
        _tokens = tokens;
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
                            updated_at = @Now
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
                DO UPDATE SET followed_at = @Now, unfollowed_at = NULL
                """,
                new { PlaylistUuid = playlistUuid, UserUuid = userUuid, Now = DateTimeOffset.UtcNow },
                transaction);
        }

        await transaction.CommitAsync();
        return await GetViewerState(userUuid, playlistUuid)
            ?? throw new InvalidOperationException("Followed playlist could not be loaded.");
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
            DO UPDATE SET role = 'editor', accepted_at = @Now, revoked_at = NULL
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
