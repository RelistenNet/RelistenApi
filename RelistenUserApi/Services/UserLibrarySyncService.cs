using System.Globalization;
using Dapper;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Npgsql;
using Relisten.UserApi.Models;

namespace Relisten.UserApi.Services;

public sealed class UserLibrarySyncService
{
    private readonly UserApiDbService _db;

    public UserLibrarySyncService(UserApiDbService db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<FavoriteResponse>> ListFavorites(Guid userUuid)
    {
        await using var connection = _db.CreateConnection();
        var rows = await connection.QueryAsync<FavoriteRow>(
            """
            SELECT
                entity_type AS "EntityType",
                entity_uuid AS "EntityUuid",
                created_at AS "CreatedAt",
                updated_at AS "UpdatedAt",
                sync_version AS "SyncVersion"
            FROM user_data.user_favorites
            WHERE user_id = @UserUuid
              AND deleted_at IS NULL
            ORDER BY entity_type, entity_uuid
            """,
            new { UserUuid = userUuid });

        return rows.Select(ToFavoriteResponse).ToList();
    }

    public async Task<FavoriteResponse> AddFavorite(Guid userUuid, string entityType, Guid entityUuid)
    {
        var normalizedEntityType = NormalizeFavoriteEntityType(entityType);
        await using var connection = _db.CreateConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await LockUserSyncScope(connection, transaction, userUuid);
        var now = DateTimeOffset.UtcNow;
        var row = await connection.QuerySingleAsync<FavoriteRow>(
            """
            INSERT INTO user_data.user_favorites
                (user_id, entity_type, entity_uuid, created_at, updated_at, deleted_at)
            VALUES
                (@UserUuid, @EntityType, @EntityUuid, @Now, @Now, NULL)
            ON CONFLICT (user_id, entity_type, entity_uuid)
            DO UPDATE SET
                updated_at = CASE
                    WHEN user_data.user_favorites.deleted_at IS NULL
                    THEN user_data.user_favorites.updated_at
                    ELSE @Now
                END,
                deleted_at = NULL,
                sync_version = CASE
                    WHEN user_data.user_favorites.deleted_at IS NULL
                    THEN user_data.user_favorites.sync_version
                    ELSE nextval('user_data.user_sync_version_seq')
                END
            RETURNING
                entity_type AS "EntityType",
                entity_uuid AS "EntityUuid",
                created_at AS "CreatedAt",
                updated_at AS "UpdatedAt",
                sync_version AS "SyncVersion"
            """,
            new
            {
                UserUuid = userUuid,
                EntityType = normalizedEntityType,
                EntityUuid = entityUuid,
                Now = now
            },
            transaction);

        await transaction.CommitAsync();
        return ToFavoriteResponse(row);
    }

    public async Task RemoveFavorite(Guid userUuid, string entityType, Guid entityUuid)
    {
        var normalizedEntityType = NormalizeFavoriteEntityType(entityType);
        await using var connection = _db.CreateConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await LockUserSyncScope(connection, transaction, userUuid);
        var now = DateTimeOffset.UtcNow;
        await connection.ExecuteAsync(
            """
            INSERT INTO user_data.user_favorites
                (user_id, entity_type, entity_uuid, created_at, updated_at, deleted_at)
            VALUES
                (@UserUuid, @EntityType, @EntityUuid, @Now, @Now, @Now)
            ON CONFLICT (user_id, entity_type, entity_uuid)
            DO UPDATE SET
                updated_at = CASE
                    WHEN user_data.user_favorites.deleted_at IS NULL
                    THEN @Now
                    ELSE user_data.user_favorites.updated_at
                END,
                deleted_at = COALESCE(user_data.user_favorites.deleted_at, @Now),
                sync_version = CASE
                    WHEN user_data.user_favorites.deleted_at IS NULL
                    THEN nextval('user_data.user_sync_version_seq')
                    ELSE user_data.user_favorites.sync_version
                END
            """,
            new
            {
                UserUuid = userUuid,
                EntityType = normalizedEntityType,
                EntityUuid = entityUuid,
                Now = now
            },
            transaction);
        await transaction.CommitAsync();
    }

    public async Task<UserSettingsResponse> GetSettings(Guid userUuid)
    {
        await using var connection = _db.CreateConnection();
        var row = await connection.QuerySingleOrDefaultAsync<SettingsRow>(
            """
            SELECT
                settings::text AS "SettingsJson",
                updated_at AS "UpdatedAt",
                sync_version AS "SyncVersion"
            FROM user_data.user_settings
            WHERE user_id = @UserUuid
            """,
            new { UserUuid = userUuid });

        return row == null
            ? new UserSettingsResponse { Settings = new JObject(), UpdatedAt = null }
            : ToSettingsResponse(row);
    }

    public async Task<UserSettingsResponse> UpdateSettings(Guid userUuid, UpdateUserSettingsRequest request)
    {
        if (request.Settings == null)
        {
            throw new UserLibrarySyncException("invalid_settings");
        }

        await using var connection = _db.CreateConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await LockUserSyncScope(connection, transaction, userUuid);
        var now = DateTimeOffset.UtcNow;
        var row = await connection.QuerySingleAsync<SettingsRow>(
            """
            INSERT INTO user_data.user_settings
                (user_id, settings, updated_at)
            VALUES
                (@UserUuid, CAST(@SettingsJson AS jsonb), @Now)
            ON CONFLICT (user_id)
            DO UPDATE SET
                settings = CASE
                    WHEN user_data.user_settings.settings IS DISTINCT FROM CAST(@SettingsJson AS jsonb)
                    THEN CAST(@SettingsJson AS jsonb)
                    ELSE user_data.user_settings.settings
                END,
                updated_at = CASE
                    WHEN user_data.user_settings.settings IS DISTINCT FROM CAST(@SettingsJson AS jsonb)
                    THEN @Now
                    ELSE user_data.user_settings.updated_at
                END,
                sync_version = CASE
                    WHEN user_data.user_settings.settings IS DISTINCT FROM CAST(@SettingsJson AS jsonb)
                    THEN nextval('user_data.user_sync_version_seq')
                    ELSE user_data.user_settings.sync_version
                END
            RETURNING
                settings::text AS "SettingsJson",
                updated_at AS "UpdatedAt",
                sync_version AS "SyncVersion"
            """,
            new
            {
                UserUuid = userUuid,
                SettingsJson = request.Settings.ToString(Formatting.None),
                Now = now
            },
            transaction);

        await transaction.CommitAsync();
        return ToSettingsResponse(row);
    }

    public async Task<UserLibrarySyncResponse> Pull(Guid userUuid, string? cursor)
    {
        var since = ParseCursor(cursor);
        await using var connection = _db.CreateConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await LockUserSyncScope(connection, transaction, userUuid);
        var favorites = (await connection.QueryAsync<FavoriteRow>(
                """
                SELECT
                    entity_type AS "EntityType",
                    entity_uuid AS "EntityUuid",
                    created_at AS "CreatedAt",
                    updated_at AS "UpdatedAt",
                    sync_version AS "SyncVersion"
                FROM user_data.user_favorites
                WHERE user_id = @UserUuid
                  AND deleted_at IS NULL
                  AND sync_version > @Since
                ORDER BY sync_version
                """,
                new { UserUuid = userUuid, Since = since },
                transaction))
            .ToList();
        var settings = await connection.QuerySingleOrDefaultAsync<SettingsRow>(
            """
            SELECT
                settings::text AS "SettingsJson",
                updated_at AS "UpdatedAt",
                sync_version AS "SyncVersion"
            FROM user_data.user_settings
            WHERE user_id = @UserUuid
              AND sync_version > @Since
            """,
            new { UserUuid = userUuid, Since = since },
            transaction);
        var tombstones = (await connection.QueryAsync<FavoriteTombstoneRow>(
                """
                SELECT
                    entity_type AS "EntityType",
                    entity_uuid AS "EntityUuid",
                    deleted_at AS "DeletedAt",
                    sync_version AS "SyncVersion"
                FROM user_data.user_favorites
                WHERE user_id = @UserUuid
                  AND deleted_at IS NOT NULL
                  AND sync_version > @Since
                ORDER BY sync_version
                """,
                new { UserUuid = userUuid, Since = since },
                transaction))
            .ToList();

        var changes = favorites
            .Select(row => new VersionedSyncChange(
                row.SyncVersion,
                new UserLibrarySyncChangeResponse
            {
                ResourceType = "favorite",
                Favorite = ToFavoriteResponse(row),
                UpdatedAt = row.UpdatedAt
            }))
            .ToList();
        if (settings != null)
        {
            changes.Add(
                new VersionedSyncChange(
                    settings.SyncVersion,
                    new UserLibrarySyncChangeResponse
                    {
                        ResourceType = "settings",
                        Settings = ToSettingsResponse(settings),
                        UpdatedAt = settings.UpdatedAt
                    }));
        }
        var nextCursor = NextCursor(since, favorites, settings, tombstones);
        await transaction.CommitAsync();

        return new UserLibrarySyncResponse
        {
            Changes = changes
                .OrderBy(change => change.SyncVersion)
                .Select(change => change.Response)
                .ToList(),
            Tombstones = tombstones.Select(ToTombstoneResponse).ToList(),
            NextCursor = FormatCursor(nextCursor)
        };
    }

    private static string NormalizeFavoriteEntityType(string entityType)
    {
        if (string.IsNullOrWhiteSpace(entityType))
        {
            throw new UserLibrarySyncException("invalid_favorite_entity_type");
        }

        var normalized = entityType.Trim().ToLowerInvariant();
        return normalized is "artist" or "show" or "source" or "track" or "tour" or "song"
            ? normalized
            : throw new UserLibrarySyncException("invalid_favorite_entity_type");
    }

    private static long ParseCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return 0;
        }

        if (!long.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var syncVersion) ||
            syncVersion < 0)
        {
            throw new UserLibrarySyncException("invalid_sync_cursor");
        }

        return syncVersion;
    }

    private static string FormatCursor(long cursor)
    {
        return cursor.ToString(CultureInfo.InvariantCulture);
    }

    private static long NextCursor(
        long since,
        IReadOnlyList<FavoriteRow> favorites,
        SettingsRow? settings,
        IReadOnlyList<FavoriteTombstoneRow> tombstones)
    {
        var nextCursor = since;
        foreach (var favorite in favorites)
        {
            nextCursor = Math.Max(nextCursor, favorite.SyncVersion);
        }

        if (settings != null)
        {
            nextCursor = Math.Max(nextCursor, settings.SyncVersion);
        }

        foreach (var tombstone in tombstones)
        {
            nextCursor = Math.Max(nextCursor, tombstone.SyncVersion);
        }

        return nextCursor;
    }

    private static Task LockUserSyncScope(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid userUuid)
    {
        var bytes = userUuid.ToByteArray();
        var lockKey = BitConverter.ToInt64(bytes, 0);
        return connection.ExecuteAsync(
            "SELECT pg_advisory_xact_lock(@LockKey)",
            new { LockKey = lockKey },
            transaction);
    }

    private static FavoriteResponse ToFavoriteResponse(FavoriteRow row)
    {
        return new FavoriteResponse
        {
            EntityType = row.EntityType,
            EntityUuid = row.EntityUuid,
            CreatedAt = row.CreatedAt,
            UpdatedAt = row.UpdatedAt
        };
    }

    private static UserSettingsResponse ToSettingsResponse(SettingsRow row)
    {
        return new UserSettingsResponse
        {
            Settings = JObject.Parse(row.SettingsJson),
            UpdatedAt = row.UpdatedAt
        };
    }

    private static UserLibraryTombstoneResponse ToTombstoneResponse(FavoriteTombstoneRow row)
    {
        return new UserLibraryTombstoneResponse
        {
            ResourceType = "favorite",
            EntityType = row.EntityType,
            EntityUuid = row.EntityUuid,
            DeletedAt = row.DeletedAt
        };
    }

    private sealed class FavoriteRow
    {
        public required string EntityType { get; init; }
        public required Guid EntityUuid { get; init; }
        public required DateTimeOffset CreatedAt { get; init; }
        public required DateTimeOffset UpdatedAt { get; init; }
        public required long SyncVersion { get; init; }
    }

    private sealed class SettingsRow
    {
        public required string SettingsJson { get; init; }
        public required DateTimeOffset UpdatedAt { get; init; }
        public required long SyncVersion { get; init; }
    }

    private sealed class FavoriteTombstoneRow
    {
        public required string EntityType { get; init; }
        public required Guid EntityUuid { get; init; }
        public required DateTimeOffset DeletedAt { get; init; }
        public required long SyncVersion { get; init; }
    }

    private sealed record VersionedSyncChange(long SyncVersion, UserLibrarySyncChangeResponse Response);
}

public sealed class UserLibrarySyncException : Exception
{
    public UserLibrarySyncException(string code)
        : base(code)
    {
        Code = code;
    }

    public string Code { get; }
}
