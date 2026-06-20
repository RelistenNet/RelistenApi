using Dapper;
using Npgsql;
using Relisten.UserApi.Models;

namespace Relisten.UserApi.Services;

public sealed class PostgresUserAuthStore : IUserAuthStore
{
    private readonly UserApiDbService _db;

    public PostgresUserAuthStore(UserApiDbService db)
    {
        _db = db;
    }

    public async Task<(UserAccount User, UserAuthMethod AuthMethod)?> FindUserByProvider(
        string provider,
        string providerSubject)
    {
        await using var connection = _db.CreateConnection();
        var row = await connection.QuerySingleOrDefaultAsync<UserAuthRow>(
            """
            SELECT
                u.id AS "UserUuid",
                u.username AS "Username",
                u.display_name AS "DisplayName",
                u.created_at AS "UserCreatedAt",
                u.updated_at AS "UserUpdatedAt",
                m.id AS "AuthMethodUuid",
                m.provider AS "Provider",
                m.provider_subject AS "ProviderSubject",
                m.created_at AS "AuthMethodCreatedAt"
            FROM user_data.user_auth_methods m
            INNER JOIN user_data.users u ON u.id = m.user_id
            WHERE m.provider = @Provider AND m.provider_subject = @ProviderSubject
            """,
            new
            {
                Provider = NormalizeProvider(provider),
                ProviderSubject = providerSubject
            });

        return row == null ? null : (row.ToUser(), row.ToAuthMethod());
    }

    public async Task<(UserAccount User, UserAuthMethod AuthMethod)> CreateUserWithProvider(
        string provider,
        string providerSubject,
        string username,
        string displayName,
        DateTimeOffset now)
    {
        var user = new UserAccount
        {
            UserUuid = UserDataUuid.New(),
            Username = username,
            DisplayName = displayName,
            CreatedAt = now,
            UpdatedAt = now
        };
        var authMethod = new UserAuthMethod
        {
            AuthMethodUuid = UserDataUuid.New(),
            UserUuid = user.UserUuid,
            Provider = NormalizeProvider(provider),
            ProviderSubject = providerSubject,
            CreatedAt = now
        };

        await using var connection = _db.CreateConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        try
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO user_data.users
                    (id, username, username_lower, display_name, created_at, updated_at)
                VALUES
                    (@UserUuid, @Username, @UsernameLower, @DisplayName, @Now, @Now)
                """,
                new
                {
                    user.UserUuid,
                    user.Username,
                    UsernameLower = user.Username.ToLowerInvariant(),
                    user.DisplayName,
                    Now = now
                },
                transaction);

            await connection.ExecuteAsync(
                """
                INSERT INTO user_data.user_auth_methods
                    (id, user_id, provider, provider_subject, created_at)
                VALUES
                    (@AuthMethodUuid, @UserUuid, @Provider, @ProviderSubject, @Now)
                """,
                new
                {
                    authMethod.AuthMethodUuid,
                    authMethod.UserUuid,
                    authMethod.Provider,
                    authMethod.ProviderSubject,
                    Now = now
                },
                transaction);

            await transaction.CommitAsync();
            return (user, authMethod);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync();
            throw new UserAuthException(
                string.Equals(ex.ConstraintName, "users_username_lower_key", StringComparison.Ordinal)
                    ? "username_taken"
                    : "provider_subject_exists");
        }
    }

    public async Task<UserAccount?> FindUser(Guid userUuid)
    {
        await using var connection = _db.CreateConnection();
        return await connection.QuerySingleOrDefaultAsync<UserAccount>(
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
    }

    public async Task<UserSession> CreateSession(Guid userUuid, DeviceDescriptor device, DateTimeOffset now)
    {
        var session = new UserSession
        {
            SessionUuid = UserDataUuid.New(),
            UserUuid = userUuid,
            DeviceId = device.DeviceId,
            DeviceName = device.DeviceName,
            Platform = device.Platform,
            LastUsedAt = now,
            CreatedAt = now,
            ReauthenticatedAt = now
        };

        await using var connection = _db.CreateConnection();
        await connection.ExecuteAsync(
            """
            INSERT INTO user_data.user_sessions
                (id, user_id, device_id, device_name, platform, last_used_at, created_at, reauthenticated_at)
            VALUES
                (@SessionUuid, @UserUuid, @DeviceId, @DeviceName, @Platform, @LastUsedAt, @CreatedAt, @ReauthenticatedAt)
            """,
            session);

        return session;
    }

    public async Task<UserSession?> GetSession(Guid sessionUuid)
    {
        await using var connection = _db.CreateConnection();
        return await connection.QuerySingleOrDefaultAsync<UserSession>(
            """
            SELECT
                id AS "SessionUuid",
                user_id AS "UserUuid",
                device_id AS "DeviceId",
                device_name AS "DeviceName",
                platform AS "Platform",
                last_used_at AS "LastUsedAt",
                created_at AS "CreatedAt",
                reauthenticated_at AS "ReauthenticatedAt",
                revoked_at AS "RevokedAt"
            FROM user_data.user_sessions
            WHERE id = @SessionUuid
            """,
            new { SessionUuid = sessionUuid });
    }

    public async Task<IReadOnlyList<UserSession>> ListSessions(Guid userUuid)
    {
        await using var connection = _db.CreateConnection();
        var sessions = await connection.QueryAsync<UserSession>(
            """
            SELECT
                id AS "SessionUuid",
                user_id AS "UserUuid",
                device_id AS "DeviceId",
                device_name AS "DeviceName",
                platform AS "Platform",
                last_used_at AS "LastUsedAt",
                created_at AS "CreatedAt",
                reauthenticated_at AS "ReauthenticatedAt",
                revoked_at AS "RevokedAt"
            FROM user_data.user_sessions
            WHERE user_id = @UserUuid AND revoked_at IS NULL
            ORDER BY last_used_at DESC
            """,
            new { UserUuid = userUuid });

        return sessions.ToList();
    }

    public async Task RevokeSession(Guid userUuid, Guid sessionUuid, DateTimeOffset now)
    {
        await using var connection = _db.CreateConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var affected = await connection.ExecuteAsync(
            """
            UPDATE user_data.user_sessions
            SET revoked_at = COALESCE(revoked_at, @Now)
            WHERE id = @SessionUuid AND user_id = @UserUuid
            """,
            new { SessionUuid = sessionUuid, UserUuid = userUuid, Now = now },
            transaction);

        if (affected > 0)
        {
            await RevokeActiveRefreshTokens(connection, transaction, sessionUuid, now);
        }

        await transaction.CommitAsync();
    }

    public async Task RevokeSessionByRefreshToken(Guid refreshTokenUuid, DateTimeOffset now)
    {
        await using var connection = _db.CreateConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var sessionUuid = await connection.QuerySingleOrDefaultAsync<Guid?>(
            """
            SELECT session_id
            FROM user_data.refresh_tokens
            WHERE id = @RefreshTokenUuid
            """,
            new { RefreshTokenUuid = refreshTokenUuid },
            transaction);

        if (sessionUuid.HasValue)
        {
            await connection.ExecuteAsync(
                """
                UPDATE user_data.user_sessions
                SET revoked_at = COALESCE(revoked_at, @Now)
                WHERE id = @SessionUuid
                """,
                new { SessionUuid = sessionUuid.Value, Now = now },
                transaction);
            await RevokeActiveRefreshTokens(connection, transaction, sessionUuid.Value, now);
        }

        await transaction.CommitAsync();
    }

    public async Task TouchSession(Guid sessionUuid, DateTimeOffset now)
    {
        await using var connection = _db.CreateConnection();
        await connection.ExecuteAsync(
            """
            UPDATE user_data.user_sessions
            SET last_used_at = @Now
            WHERE id = @SessionUuid AND revoked_at IS NULL
            """,
            new { SessionUuid = sessionUuid, Now = now });
    }

    public async Task MarkSessionReauthenticated(Guid userUuid, Guid sessionUuid, DateTimeOffset now)
    {
        await using var connection = _db.CreateConnection();
        await connection.ExecuteAsync(
            """
            UPDATE user_data.user_sessions
            SET reauthenticated_at = @Now,
                last_used_at = @Now
            WHERE id = @SessionUuid
              AND user_id = @UserUuid
              AND revoked_at IS NULL
            """,
            new { UserUuid = userUuid, SessionUuid = sessionUuid, Now = now });
    }

    public async Task<RefreshTokenRecord> AddRefreshToken(Guid sessionUuid, RefreshToken token)
    {
        var record = new RefreshTokenRecord
        {
            RefreshTokenUuid = UserDataUuid.New(),
            SessionUuid = sessionUuid,
            Selector = token.Selector,
            SecretHash = token.SecretHash,
            Status = RefreshTokenStatus.Active,
            IssuedAt = token.IssuedAt,
            ExpiresAt = token.ExpiresAt
        };

        await using var connection = _db.CreateConnection();
        await InsertRefreshToken(connection, transaction: null, record);

        return record;
    }

    public async Task<RefreshTokenRecord?> RotateRefreshToken(
        Guid currentRefreshTokenUuid,
        Guid sessionUuid,
        RefreshToken replacementToken,
        DateTimeOffset now)
    {
        await using var connection = _db.CreateConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var current = await connection.QuerySingleOrDefaultAsync<RefreshTokenRow>(
            """
            SELECT
                id AS "RefreshTokenUuid",
                session_id AS "SessionUuid",
                token_selector AS "Selector",
                token_secret_hash AS "SecretHash",
                status AS "Status",
                issued_at AS "IssuedAt",
                expires_at AS "ExpiresAt",
                rotated_at AS "RotatedAt",
                replaced_by_token_id AS "ReplacedByTokenUuid",
                reuse_detected_at AS "ReuseDetectedAt"
            FROM user_data.refresh_tokens
            WHERE id = @RefreshTokenUuid AND session_id = @SessionUuid
            FOR UPDATE
            """,
            new { RefreshTokenUuid = currentRefreshTokenUuid, SessionUuid = sessionUuid },
            transaction);

        if (current == null || ToStatus(current.Status) != RefreshTokenStatus.Active)
        {
            await transaction.RollbackAsync();
            return null;
        }

        var replacement = NewRefreshTokenRecord(sessionUuid, replacementToken);
        await InsertRefreshToken(connection, transaction, replacement);

        await connection.ExecuteAsync(
            """
            UPDATE user_data.refresh_tokens
            SET status = @Status,
                rotated_at = @Now,
                replaced_by_token_id = @ReplacementRefreshTokenUuid
            WHERE id = @RefreshTokenUuid
            """,
            new
            {
                RefreshTokenUuid = currentRefreshTokenUuid,
                ReplacementRefreshTokenUuid = replacement.RefreshTokenUuid,
                Status = ToDbStatus(RefreshTokenStatus.Rotated),
                Now = now
            },
            transaction);

        await transaction.CommitAsync();
        return replacement;
    }

    public async Task<RefreshTokenRecord?> FindRefreshTokenBySelector(string selector)
    {
        await using var connection = _db.CreateConnection();
        var row = await connection.QuerySingleOrDefaultAsync<RefreshTokenRow>(
            """
            SELECT
                id AS "RefreshTokenUuid",
                session_id AS "SessionUuid",
                token_selector AS "Selector",
                token_secret_hash AS "SecretHash",
                status AS "Status",
                issued_at AS "IssuedAt",
                expires_at AS "ExpiresAt",
                rotated_at AS "RotatedAt",
                replaced_by_token_id AS "ReplacedByTokenUuid",
                reuse_detected_at AS "ReuseDetectedAt"
            FROM user_data.refresh_tokens
            WHERE token_selector = @Selector
            """,
            new { Selector = selector });

        return row?.ToRecord();
    }

    public async Task MarkRefreshTokenReuseDetected(Guid refreshTokenUuid, DateTimeOffset now)
    {
        await using var connection = _db.CreateConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var sessionUuid = await connection.QuerySingleOrDefaultAsync<Guid?>(
            """
            UPDATE user_data.refresh_tokens
            SET status = @Status,
                reuse_detected_at = @Now
            WHERE id = @RefreshTokenUuid
            RETURNING session_id
            """,
            new
            {
                RefreshTokenUuid = refreshTokenUuid,
                Status = ToDbStatus(RefreshTokenStatus.ReuseDetected),
                Now = now
            },
            transaction);

        if (sessionUuid.HasValue)
        {
            await connection.ExecuteAsync(
                """
                UPDATE user_data.user_sessions
                SET revoked_at = COALESCE(revoked_at, @Now)
                WHERE id = @SessionUuid
                """,
                new { SessionUuid = sessionUuid.Value, Now = now },
                transaction);
            await RevokeActiveRefreshTokens(connection, transaction, sessionUuid.Value, now);
        }

        await transaction.CommitAsync();
    }

    private static Task RevokeActiveRefreshTokens(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid sessionUuid,
        DateTimeOffset now)
    {
        return connection.ExecuteAsync(
            """
            UPDATE user_data.refresh_tokens
            SET status = @Status,
                rotated_at = COALESCE(rotated_at, @Now)
            WHERE session_id = @SessionUuid AND status = @ActiveStatus
            """,
            new
            {
                SessionUuid = sessionUuid,
                Status = ToDbStatus(RefreshTokenStatus.Revoked),
                ActiveStatus = ToDbStatus(RefreshTokenStatus.Active),
                Now = now
            },
            transaction);
    }

    private static Task<int> InsertRefreshToken(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        RefreshTokenRecord record)
    {
        return connection.ExecuteAsync(
            """
            INSERT INTO user_data.refresh_tokens
                (id, session_id, token_selector, token_secret_hash, status, issued_at, expires_at)
            VALUES
                (@RefreshTokenUuid, @SessionUuid, @Selector, @SecretHash, @Status, @IssuedAt, @ExpiresAt)
            """,
            new
            {
                record.RefreshTokenUuid,
                record.SessionUuid,
                record.Selector,
                record.SecretHash,
                Status = ToDbStatus(record.Status),
                record.IssuedAt,
                record.ExpiresAt
            },
            transaction);
    }

    private static string NormalizeProvider(string provider)
    {
        return provider.Trim().ToLowerInvariant();
    }

    private static string ToDbStatus(RefreshTokenStatus status)
    {
        return status switch
        {
            RefreshTokenStatus.Active => "active",
            RefreshTokenStatus.Rotated => "rotated",
            RefreshTokenStatus.Revoked => "revoked",
            RefreshTokenStatus.ReuseDetected => "reuse_detected",
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
        };
    }

    private static RefreshTokenRecord NewRefreshTokenRecord(Guid sessionUuid, RefreshToken token)
    {
        return new RefreshTokenRecord
        {
            RefreshTokenUuid = UserDataUuid.New(),
            SessionUuid = sessionUuid,
            Selector = token.Selector,
            SecretHash = token.SecretHash,
            Status = RefreshTokenStatus.Active,
            IssuedAt = token.IssuedAt,
            ExpiresAt = token.ExpiresAt
        };
    }

    private static RefreshTokenStatus ToStatus(string status)
    {
        return status switch
        {
            "active" => RefreshTokenStatus.Active,
            "rotated" => RefreshTokenStatus.Rotated,
            "revoked" => RefreshTokenStatus.Revoked,
            "reuse_detected" => RefreshTokenStatus.ReuseDetected,
            _ => throw new InvalidOperationException($"Unknown refresh token status '{status}'.")
        };
    }

    private sealed class UserAuthRow
    {
        public required Guid UserUuid { get; init; }
        public required string Username { get; init; }
        public required string DisplayName { get; init; }
        public required DateTimeOffset UserCreatedAt { get; init; }
        public required DateTimeOffset UserUpdatedAt { get; init; }
        public required Guid AuthMethodUuid { get; init; }
        public required string Provider { get; init; }
        public required string ProviderSubject { get; init; }
        public required DateTimeOffset AuthMethodCreatedAt { get; init; }

        public UserAccount ToUser()
        {
            return new UserAccount
            {
                UserUuid = UserUuid,
                Username = Username,
                DisplayName = DisplayName,
                CreatedAt = UserCreatedAt,
                UpdatedAt = UserUpdatedAt
            };
        }

        public UserAuthMethod ToAuthMethod()
        {
            return new UserAuthMethod
            {
                AuthMethodUuid = AuthMethodUuid,
                UserUuid = UserUuid,
                Provider = Provider,
                ProviderSubject = ProviderSubject,
                CreatedAt = AuthMethodCreatedAt
            };
        }
    }

    private sealed class RefreshTokenRow
    {
        public required Guid RefreshTokenUuid { get; init; }
        public required Guid SessionUuid { get; init; }
        public required string Selector { get; init; }
        public required string SecretHash { get; init; }
        public required string Status { get; init; }
        public required DateTimeOffset IssuedAt { get; init; }
        public required DateTimeOffset ExpiresAt { get; init; }
        public DateTimeOffset? RotatedAt { get; init; }
        public Guid? ReplacedByTokenUuid { get; init; }
        public DateTimeOffset? ReuseDetectedAt { get; init; }

        public RefreshTokenRecord ToRecord()
        {
            return new RefreshTokenRecord
            {
                RefreshTokenUuid = RefreshTokenUuid,
                SessionUuid = SessionUuid,
                Selector = Selector,
                SecretHash = SecretHash,
                Status = ToStatus(Status),
                IssuedAt = IssuedAt,
                ExpiresAt = ExpiresAt,
                RotatedAt = RotatedAt,
                ReplacedByTokenUuid = ReplacedByTokenUuid,
                ReuseDetectedAt = ReuseDetectedAt
            };
        }
    }
}
