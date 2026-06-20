using Dapper;
using Relisten.UserApi.Models;

namespace Relisten.UserApi.Services;

public sealed class UserAccountDataService
{
    private readonly UserApiDbService _db;
    private readonly PlaylistService _playlists;
    private readonly UserLibrarySyncService _sync;

    public UserAccountDataService(
        UserApiDbService db,
        PlaylistService playlists,
        UserLibrarySyncService sync)
    {
        _db = db;
        _playlists = playlists;
        _sync = sync;
    }

    public async Task<AccountExportResponse> Export(Guid userUuid)
    {
        await using var connection = _db.CreateConnection();
        var user = await connection.QuerySingleAsync<UserAccount>(
            """
            SELECT
                id AS "UserUuid",
                username AS "Username",
                display_name AS "DisplayName",
                created_at AS "CreatedAt",
                updated_at AS "UpdatedAt"
            FROM user_data.users
            WHERE id = @UserUuid
            """,
            new { UserUuid = userUuid });

        return new AccountExportResponse
        {
            ExportedAt = DateTimeOffset.UtcNow,
            User = new CurrentUserResponse
            {
                UserUuid = user.UserUuid,
                DisplayName = user.DisplayName,
                Username = user.Username,
                ScopeId = $"user:{user.UserUuid}"
            },
            AuthMethods = await LoadAuthMethods(connection, userUuid),
            Sessions = await LoadSessions(connection, userUuid),
            Favorites = await LoadFavorites(connection, userUuid),
            Settings = await _sync.GetSettings(userUuid),
            Playlists = await _playlists.ListForUser(userUuid),
            PlaybackHistory = await LoadPlaybackHistory(connection, userUuid)
        };
    }

    public async Task Delete(Guid userUuid)
    {
        await using var connection = _db.CreateConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var lockedUserUuid = await connection.QuerySingleOrDefaultAsync<Guid?>(
            """
            SELECT id
            FROM user_data.users
            WHERE id = @UserUuid
            FOR UPDATE
            """,
            new { UserUuid = userUuid },
            transaction);
        if (!lockedUserUuid.HasValue)
        {
            await transaction.CommitAsync();
            return;
        }

        // Owned playlist content still has user FKs on entries/blocks; delete those playlists
        // while the user exists so playlist cascades remove their contents first.
        await connection.ExecuteAsync(
            """
            DELETE FROM user_data.playlists
            WHERE owner_id = @UserUuid
            """,
            new { UserUuid = userUuid },
            transaction);
        await connection.ExecuteAsync(
            """
            DELETE FROM user_data.playlist_share_tokens
            WHERE created_by = @UserUuid
            """,
            new { UserUuid = userUuid },
            transaction);
        await connection.ExecuteAsync(
            """
            UPDATE user_data.playlist_collaborators
            SET invited_by = NULL
            WHERE invited_by = @UserUuid
            """,
            new { UserUuid = userUuid },
            transaction);
        await connection.ExecuteAsync(
            """
            DELETE FROM user_data.playlist_edit_log
            WHERE user_id = @UserUuid
            """,
            new { UserUuid = userUuid },
            transaction);
        await connection.ExecuteAsync(
            """
            UPDATE user_data.playlist_blocks b
            SET created_by = p.owner_id
            FROM user_data.playlists p
            WHERE b.playlist_id = p.id
              AND b.created_by = @UserUuid
              AND p.owner_id <> @UserUuid
            """,
            new { UserUuid = userUuid },
            transaction);
        await connection.ExecuteAsync(
            """
            UPDATE user_data.playlist_entries e
            SET added_by = p.owner_id
            FROM user_data.playlists p
            WHERE e.playlist_id = p.id
              AND e.added_by = @UserUuid
              AND p.owner_id <> @UserUuid
            """,
            new { UserUuid = userUuid },
            transaction);
        await connection.ExecuteAsync(
            """
            DELETE FROM user_data.users
            WHERE id = @UserUuid
            """,
            new { UserUuid = userUuid },
            transaction);

        var historyRows = await connection.QuerySingleAsync<int>(
            """
            SELECT count(*)::int
            FROM user_data.playback_history
            WHERE user_id = @UserUuid
            """,
            new { UserUuid = userUuid },
            transaction);
        if (historyRows != 0)
        {
            await transaction.RollbackAsync();
            throw new UserAccountDeletionException("account_deletion_incomplete");
        }

        await transaction.CommitAsync();
    }

    private static async Task<IReadOnlyList<AccountAuthMethodExportResponse>> LoadAuthMethods(
        Npgsql.NpgsqlConnection connection,
        Guid userUuid)
    {
        var rows = await connection.QueryAsync<AccountAuthMethodExportResponse>(
            """
            SELECT
                id AS "AuthMethodUuid",
                provider AS "Provider",
                provider_subject AS "ProviderSubject",
                created_at AS "CreatedAt"
            FROM user_data.user_auth_methods
            WHERE user_id = @UserUuid
            ORDER BY created_at ASC
            """,
            new { UserUuid = userUuid });
        return rows.ToList();
    }

    private static async Task<IReadOnlyList<AccountSessionExportResponse>> LoadSessions(
        Npgsql.NpgsqlConnection connection,
        Guid userUuid)
    {
        var rows = await connection.QueryAsync<AccountSessionExportResponse>(
            """
            SELECT
                id AS "SessionUuid",
                device_id AS "DeviceId",
                device_name AS "DeviceName",
                platform AS "Platform",
                last_used_at AS "LastUsedAt",
                created_at AS "CreatedAt",
                reauthenticated_at AS "ReauthenticatedAt",
                revoked_at AS "RevokedAt"
            FROM user_data.user_sessions
            WHERE user_id = @UserUuid
            ORDER BY created_at ASC
            """,
            new { UserUuid = userUuid });
        return rows.ToList();
    }

    private static async Task<IReadOnlyList<AccountFavoriteExportResponse>> LoadFavorites(
        Npgsql.NpgsqlConnection connection,
        Guid userUuid)
    {
        var rows = await connection.QueryAsync<AccountFavoriteExportResponse>(
            """
            SELECT
                entity_type AS "EntityType",
                entity_uuid AS "EntityUuid",
                created_at AS "CreatedAt",
                updated_at AS "UpdatedAt",
                deleted_at AS "DeletedAt"
            FROM user_data.user_favorites
            WHERE user_id = @UserUuid
            ORDER BY entity_type ASC, entity_uuid ASC
            """,
            new { UserUuid = userUuid });
        return rows.ToList();
    }

    private static async Task<IReadOnlyList<AccountPlaybackHistoryExportResponse>> LoadPlaybackHistory(
        Npgsql.NpgsqlConnection connection,
        Guid userUuid)
    {
        var rows = await connection.QueryAsync<AccountPlaybackHistoryExportResponse>(
            """
            SELECT
                id AS "HistoryUuid",
                client_event_uuid AS "ClientEventUuid",
                source_track_uuid AS "SourceTrackUuid",
                source_uuid AS "SourceUuid",
                playlist_uuid AS "PlaylistUuid",
                playlist_entry_uuid AS "PlaylistEntryUuid",
                block_uuid AS "BlockUuid",
                block_position AS "BlockPosition",
                played_at AS "PlayedAt",
                platform AS "Platform",
                app_version AS "AppVersion",
                device_id AS "DeviceId"
            FROM user_data.playback_history
            WHERE user_id = @UserUuid
            ORDER BY played_at DESC, id DESC
            """,
            new { UserUuid = userUuid });
        return rows.ToList();
    }
}

public sealed class UserAccountDeletionException : Exception
{
    public UserAccountDeletionException(string code)
        : base(code)
    {
        Code = code;
    }

    public string Code { get; }
}
