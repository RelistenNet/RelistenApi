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
        var favoriteTombstones = (await connection.QueryAsync<FavoriteTombstoneRow>(
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
        var playlists = await LoadPlaylistChanges(connection, transaction, userUuid, since);
        var entriesByPlaylist = await LoadEntriesByPlaylist(connection, transaction, playlists);
        var pendingInvitations = await LoadPendingInvitations(connection, transaction, userUuid, since);
        var ownerCollaborators = await LoadOwnerCollaboratorChanges(connection, transaction, userUuid, since);
        var playlistTombstones = await LoadPlaylistTombstones(connection, transaction, userUuid, since);

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

        foreach (var playlist in playlists)
        {
            changes.Add(
                new VersionedSyncChange(
                    playlist.SyncVersion,
                    new UserLibrarySyncChangeResponse
                    {
                        ResourceType = "playlist",
                        Playlist = ToPlaylistResponse(
                            playlist,
                            entriesByPlaylist.GetValueOrDefault(playlist.PlaylistUuid, [])),
                        PlaylistViewerState = ToViewerStateResponse(playlist),
                        UpdatedAt = playlist.UpdatedAt
                    }));
        }

        foreach (var invitation in pendingInvitations)
        {
            changes.Add(
                new VersionedSyncChange(
                    invitation.SyncVersion,
                    new UserLibrarySyncChangeResponse
                    {
                        ResourceType = "collaborator_invitation",
                        Collaborator = ToCollaboratorResponse(invitation),
                        UpdatedAt = invitation.UpdatedAt
                    }));
        }

        foreach (var collaborator in ownerCollaborators)
        {
            changes.Add(
                new VersionedSyncChange(
                    collaborator.SyncVersion,
                    new UserLibrarySyncChangeResponse
                    {
                        ResourceType = "playlist_collaborator",
                        Collaborator = ToCollaboratorResponse(collaborator),
                        UpdatedAt = collaborator.UpdatedAt
                    }));
        }

        var tombstones = favoriteTombstones
            .Select(row => new VersionedTombstone(row.SyncVersion, ToTombstoneResponse(row)))
            .Concat(playlistTombstones.Select(row => new VersionedTombstone(row.SyncVersion, ToTombstoneResponse(row))))
            .ToList();
        var nextCursor = NextCursor(
            since,
            favorites.Select(row => row.SyncVersion),
            settings == null ? Enumerable.Empty<long>() : [settings.SyncVersion],
            favoriteTombstones.Select(row => row.SyncVersion),
            playlists.Select(row => row.SyncVersion),
            pendingInvitations.Select(row => row.SyncVersion),
            ownerCollaborators.Select(row => row.SyncVersion),
            playlistTombstones.Select(row => row.SyncVersion));
        await transaction.CommitAsync();

        return new UserLibrarySyncResponse
        {
            Changes = changes
                .OrderBy(change => change.SyncVersion)
                .Select(change => change.Response)
                .ToList(),
            Tombstones = tombstones
                .OrderBy(tombstone => tombstone.SyncVersion)
                .Select(tombstone => tombstone.Response)
                .ToList(),
            NextCursor = FormatCursor(nextCursor)
        };
    }

    private static async Task<IReadOnlyList<PlaylistSyncRow>> LoadPlaylistChanges(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid userUuid,
        long since)
    {
        var rows = await connection.QueryAsync<PlaylistSyncRow>(
            """
            SELECT
                p.id AS "PlaylistUuid",
                p.short_id AS "ShortId",
                p.owner_id AS "OwnerUserUuid",
                p.name AS "Name",
                p.description AS "Description",
                p.visibility AS "Visibility",
                p.current_revision AS "CurrentRevision",
                p.created_at AS "CreatedAt",
                p.updated_at AS "UpdatedAt",
                (p.owner_id = @UserUuid) AS "IsOwner",
                (c.user_id IS NOT NULL) AS "IsCollaborator",
                (f.user_id IS NOT NULL) AS "IsFollowing",
                GREATEST(
                    p.sync_version,
                    COALESCE(c.sync_version, 0),
                    COALESCE(f.sync_version, 0)
                ) AS "SyncVersion"
            FROM user_data.playlists p
            LEFT JOIN user_data.playlist_collaborators c
              ON c.playlist_id = p.id
             AND c.user_id = @UserUuid
             AND c.accepted_at IS NOT NULL
             AND c.revoked_at IS NULL
            LEFT JOIN user_data.playlist_followers f
              ON f.playlist_id = p.id
             AND f.user_id = @UserUuid
             AND f.unfollowed_at IS NULL
            WHERE p.archived_at IS NULL
              AND (
                  p.owner_id = @UserUuid
                  OR c.user_id IS NOT NULL
                  OR f.user_id IS NOT NULL
              )
              AND GREATEST(
                    p.sync_version,
                    COALESCE(c.sync_version, 0),
                    COALESCE(f.sync_version, 0)
                  ) > @Since
            ORDER BY "SyncVersion"
            """,
            new { UserUuid = userUuid, Since = since },
            transaction);

        return rows.ToList();
    }

    private static async Task<IReadOnlyDictionary<Guid, IReadOnlyList<PlaylistEntryRecord>>> LoadEntriesByPlaylist(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<PlaylistSyncRow> playlists)
    {
        if (playlists.Count == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<PlaylistEntryRecord>>();
        }

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
            new { PlaylistUuids = playlists.Select(playlist => playlist.PlaylistUuid).ToArray() },
            transaction);

        return entries
            .GroupBy(entry => entry.PlaylistUuid)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<PlaylistEntryRecord>)group.ToList());
    }

    private static async Task<IReadOnlyList<CollaboratorSyncRow>> LoadPendingInvitations(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid userUuid,
        long since)
    {
        var rows = await connection.QueryAsync<CollaboratorSyncRow>(
            """
            SELECT
                c.playlist_id AS "PlaylistUuid",
                c.user_id AS "UserUuid",
                u.username AS "Username",
                c.role AS "Role",
                c.invited_by AS "InvitedByUserUuid",
                c.invited_at AS "InvitedAt",
                c.accepted_at AS "AcceptedAt",
                c.revoked_at AS "RevokedAt",
                c.invited_at AS "UpdatedAt",
                c.sync_version AS "SyncVersion"
            FROM user_data.playlist_collaborators c
            INNER JOIN user_data.playlists p ON p.id = c.playlist_id
            INNER JOIN user_data.users u ON u.id = c.user_id
            WHERE c.user_id = @UserUuid
              AND c.accepted_at IS NULL
              AND c.revoked_at IS NULL
              AND p.archived_at IS NULL
              AND c.sync_version > @Since
            ORDER BY c.sync_version
            """,
            new { UserUuid = userUuid, Since = since },
            transaction);

        return rows.ToList();
    }

    private static async Task<IReadOnlyList<CollaboratorSyncRow>> LoadOwnerCollaboratorChanges(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid userUuid,
        long since)
    {
        var rows = await connection.QueryAsync<CollaboratorSyncRow>(
            """
            SELECT
                c.playlist_id AS "PlaylistUuid",
                c.user_id AS "UserUuid",
                u.username AS "Username",
                c.role AS "Role",
                c.invited_by AS "InvitedByUserUuid",
                c.invited_at AS "InvitedAt",
                c.accepted_at AS "AcceptedAt",
                c.revoked_at AS "RevokedAt",
                COALESCE(c.accepted_at, c.invited_at) AS "UpdatedAt",
                c.sync_version AS "SyncVersion"
            FROM user_data.playlist_collaborators c
            INNER JOIN user_data.playlists p ON p.id = c.playlist_id
            INNER JOIN user_data.users u ON u.id = c.user_id
            WHERE p.owner_id = @UserUuid
              AND p.archived_at IS NULL
              AND c.revoked_at IS NULL
              AND c.sync_version > @Since
            ORDER BY c.sync_version
            """,
            new { UserUuid = userUuid, Since = since },
            transaction);

        return rows.ToList();
    }

    private static async Task<IReadOnlyList<PlaylistTombstoneRow>> LoadPlaylistTombstones(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid userUuid,
        long since)
    {
        var rows = await connection.QueryAsync<PlaylistTombstoneRow>(
            """
            SELECT
                'playlist_collaborator' AS "ResourceType",
                c.playlist_id AS "PlaylistUuid",
                c.user_id AS "UserUuid",
                c.revoked_at AS "DeletedAt",
                c.sync_version AS "SyncVersion"
            FROM user_data.playlist_collaborators c
            INNER JOIN user_data.playlists p ON p.id = c.playlist_id
            WHERE p.owner_id = @UserUuid
              AND c.revoked_at IS NOT NULL
              AND c.sync_version > @Since

            UNION ALL

            SELECT
                'playlist_access' AS "ResourceType",
                c.playlist_id AS "PlaylistUuid",
                c.user_id AS "UserUuid",
                c.revoked_at AS "DeletedAt",
                c.sync_version AS "SyncVersion"
            FROM user_data.playlist_collaborators c
            WHERE c.user_id = @UserUuid
              AND c.accepted_at IS NOT NULL
              AND c.revoked_at IS NOT NULL
              AND c.sync_version > @Since

            UNION ALL

            SELECT
                'collaborator_invitation' AS "ResourceType",
                c.playlist_id AS "PlaylistUuid",
                c.user_id AS "UserUuid",
                COALESCE(c.revoked_at, c.accepted_at) AS "DeletedAt",
                c.sync_version AS "SyncVersion"
            FROM user_data.playlist_collaborators c
            WHERE c.user_id = @UserUuid
              AND c.invited_by IS NOT NULL
              AND (
                  (c.accepted_at IS NULL AND c.revoked_at IS NOT NULL)
                  OR c.accepted_at IS NOT NULL
              )
              AND c.sync_version > @Since
            ORDER BY "SyncVersion"
            """,
            new { UserUuid = userUuid, Since = since },
            transaction);

        return rows.ToList();
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

    private static long NextCursor(long since, params IEnumerable<long>[] syncVersions)
    {
        var nextCursor = since;
        foreach (var versions in syncVersions)
        {
            foreach (var version in versions)
            {
                nextCursor = Math.Max(nextCursor, version);
            }
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

    private static PlaylistResponse ToPlaylistResponse(
        PlaylistSyncRow row,
        IReadOnlyList<PlaylistEntryRecord> entries)
    {
        return new PlaylistResponse
        {
            PlaylistUuid = row.PlaylistUuid,
            ShortId = row.ShortId,
            OwnerUserUuid = row.OwnerUserUuid,
            Name = row.Name,
            Description = row.Description,
            Visibility = row.Visibility,
            CurrentRevision = row.CurrentRevision,
            Entries = entries
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

    private static PlaylistViewerStateResponse ToViewerStateResponse(PlaylistSyncRow row)
    {
        return new PlaylistViewerStateResponse
        {
            IsOwner = row.IsOwner,
            IsFollowing = row.IsFollowing,
            IsCollaborator = row.IsCollaborator,
            CanEdit = row.IsOwner || row.IsCollaborator,
            AccessRole = row.IsOwner ? "owner" : row.IsCollaborator ? "editor" : "viewer"
        };
    }

    private static PlaylistCollaboratorResponse ToCollaboratorResponse(CollaboratorSyncRow row)
    {
        return new PlaylistCollaboratorResponse
        {
            PlaylistUuid = row.PlaylistUuid,
            UserUuid = row.UserUuid,
            Username = row.Username,
            Role = row.Role,
            InvitedByUserUuid = row.InvitedByUserUuid,
            InvitedAt = row.InvitedAt,
            AcceptedAt = row.AcceptedAt,
            RevokedAt = row.RevokedAt
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

    private static UserLibraryTombstoneResponse ToTombstoneResponse(PlaylistTombstoneRow row)
    {
        return new UserLibraryTombstoneResponse
        {
            ResourceType = row.ResourceType,
            PlaylistUuid = row.PlaylistUuid,
            UserUuid = row.UserUuid,
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

    private sealed class PlaylistSyncRow
    {
        public required Guid PlaylistUuid { get; init; }
        public required string ShortId { get; init; }
        public required Guid OwnerUserUuid { get; init; }
        public required string Name { get; init; }
        public string? Description { get; init; }
        public required string Visibility { get; init; }
        public required long CurrentRevision { get; init; }
        public required DateTimeOffset CreatedAt { get; init; }
        public required DateTimeOffset UpdatedAt { get; init; }
        public required bool IsOwner { get; init; }
        public required bool IsCollaborator { get; init; }
        public required bool IsFollowing { get; init; }
        public required long SyncVersion { get; init; }
    }

    private sealed class CollaboratorSyncRow
    {
        public required Guid PlaylistUuid { get; init; }
        public required Guid UserUuid { get; init; }
        public required string Username { get; init; }
        public required string Role { get; init; }
        public Guid? InvitedByUserUuid { get; init; }
        public required DateTimeOffset InvitedAt { get; init; }
        public DateTimeOffset? AcceptedAt { get; init; }
        public DateTimeOffset? RevokedAt { get; init; }
        public required DateTimeOffset UpdatedAt { get; init; }
        public required long SyncVersion { get; init; }
    }

    private sealed class PlaylistTombstoneRow
    {
        public required string ResourceType { get; init; }
        public required Guid PlaylistUuid { get; init; }
        public required Guid UserUuid { get; init; }
        public required DateTimeOffset DeletedAt { get; init; }
        public required long SyncVersion { get; init; }
    }

    private sealed record VersionedSyncChange(long SyncVersion, UserLibrarySyncChangeResponse Response);

    private sealed record VersionedTombstone(long SyncVersion, UserLibraryTombstoneResponse Response);
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
