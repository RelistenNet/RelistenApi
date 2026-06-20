using Relisten.UserApi.Models;

namespace Relisten.UserApi.Services;

public sealed class InMemoryUserAuthStore : IUserAuthStore
{
    private readonly object _lock = new();
    private readonly Dictionary<Guid, UserAccount> _users = new();
    private readonly Dictionary<(string Provider, string Subject), UserAuthMethod> _authMethodsByProvider = new();
    private readonly Dictionary<Guid, UserSession> _sessions = new();
    private readonly Dictionary<Guid, RefreshTokenRecord> _refreshTokens = new();
    private readonly Dictionary<string, Guid> _refreshTokenIdsBySelector = new(StringComparer.Ordinal);
    private readonly HashSet<string> _usernamesLower = new(StringComparer.OrdinalIgnoreCase);

    public Task<(UserAccount User, UserAuthMethod AuthMethod)?> FindUserByProvider(
        string provider,
        string providerSubject)
    {
        lock (_lock)
        {
            if (!_authMethodsByProvider.TryGetValue((NormalizeProvider(provider), providerSubject), out var authMethod))
            {
                return Task.FromResult<(UserAccount, UserAuthMethod)?>(null);
            }

            return Task.FromResult<(UserAccount, UserAuthMethod)?>((_users[authMethod.UserUuid], authMethod));
        }
    }

    public Task<(UserAccount User, UserAuthMethod AuthMethod)> CreateUserWithProvider(
        string provider,
        string providerSubject,
        string username,
        string displayName,
        DateTimeOffset now)
    {
        lock (_lock)
        {
            var key = (NormalizeProvider(provider), providerSubject);
            if (_authMethodsByProvider.ContainsKey(key))
            {
                throw new InvalidOperationException("Provider subject already exists.");
            }

            if (!_usernamesLower.Add(username.ToLowerInvariant()))
            {
                throw new UserAuthException("username_taken");
            }

            var user = new UserAccount
            {
                UserUuid = Guid.NewGuid(),
                Username = username,
                DisplayName = displayName,
                CreatedAt = now,
                UpdatedAt = now
            };
            var authMethod = new UserAuthMethod
            {
                AuthMethodUuid = Guid.NewGuid(),
                UserUuid = user.UserUuid,
                Provider = key.Item1,
                ProviderSubject = providerSubject,
                CreatedAt = now
            };

            _users.Add(user.UserUuid, user);
            _authMethodsByProvider.Add(key, authMethod);

            return Task.FromResult((user, authMethod));
        }
    }

    public Task<UserAccount?> FindUser(Guid userUuid)
    {
        lock (_lock)
        {
            return Task.FromResult(_users.GetValueOrDefault(userUuid));
        }
    }

    public Task<UserSession> CreateSession(Guid userUuid, DeviceDescriptor device, DateTimeOffset now)
    {
        lock (_lock)
        {
            var session = new UserSession
            {
                SessionUuid = Guid.NewGuid(),
                UserUuid = userUuid,
                DeviceId = device.DeviceId,
                DeviceName = device.DeviceName,
                Platform = device.Platform,
                LastUsedAt = now,
                CreatedAt = now,
                ReauthenticatedAt = now
            };

            _sessions.Add(session.SessionUuid, session);
            return Task.FromResult(session);
        }
    }

    public Task<UserSession?> GetSession(Guid sessionUuid)
    {
        lock (_lock)
        {
            return Task.FromResult(_sessions.GetValueOrDefault(sessionUuid));
        }
    }

    public Task<IReadOnlyList<UserSession>> ListSessions(Guid userUuid)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<UserSession>>(
                    _sessions.Values
                    .Where(session => session.UserUuid == userUuid && session.RevokedAt == null)
                    .OrderByDescending(session => session.LastUsedAt)
                    .ToList());
        }
    }

    public Task RevokeSession(Guid userUuid, Guid sessionUuid, DateTimeOffset now)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(sessionUuid, out var session) && session.UserUuid == userUuid)
            {
                session.RevokedAt ??= now;
                RevokeRefreshTokensForSession(sessionUuid, now);
            }
        }

        return Task.CompletedTask;
    }

    public Task RevokeSessionByRefreshToken(Guid refreshTokenUuid, DateTimeOffset now)
    {
        lock (_lock)
        {
            if (_refreshTokens.TryGetValue(refreshTokenUuid, out var token) &&
                _sessions.TryGetValue(token.SessionUuid, out var session))
            {
                session.RevokedAt ??= now;
                RevokeRefreshTokensForSession(session.SessionUuid, now);
            }
        }

        return Task.CompletedTask;
    }

    public Task TouchSession(Guid sessionUuid, DateTimeOffset now)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(sessionUuid, out var session))
            {
                session.LastUsedAt = now;
            }
        }

        return Task.CompletedTask;
    }

    public Task MarkSessionReauthenticated(Guid userUuid, Guid sessionUuid, DateTimeOffset now)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(sessionUuid, out var session) &&
                session.UserUuid == userUuid &&
                session.RevokedAt == null)
            {
                session.ReauthenticatedAt = now;
                session.LastUsedAt = now;
            }
        }

        return Task.CompletedTask;
    }

    public Task<RefreshTokenRecord> AddRefreshToken(Guid sessionUuid, RefreshToken token)
    {
        lock (_lock)
        {
            var record = NewRefreshTokenRecord(sessionUuid, token);

            _refreshTokens.Add(record.RefreshTokenUuid, record);
            _refreshTokenIdsBySelector.Add(record.Selector, record.RefreshTokenUuid);

            return Task.FromResult(record);
        }
    }

    public Task<RefreshTokenRecord?> RotateRefreshToken(
        Guid currentRefreshTokenUuid,
        Guid sessionUuid,
        RefreshToken replacementToken,
        DateTimeOffset now)
    {
        lock (_lock)
        {
            if (!_refreshTokens.TryGetValue(currentRefreshTokenUuid, out var current) ||
                current.SessionUuid != sessionUuid ||
                current.Status != RefreshTokenStatus.Active)
            {
                return Task.FromResult<RefreshTokenRecord?>(null);
            }

            var replacement = NewRefreshTokenRecord(sessionUuid, replacementToken);
            _refreshTokens.Add(replacement.RefreshTokenUuid, replacement);
            _refreshTokenIdsBySelector.Add(replacement.Selector, replacement.RefreshTokenUuid);

            current.Status = RefreshTokenStatus.Rotated;
            current.RotatedAt = now;
            current.ReplacedByTokenUuid = replacement.RefreshTokenUuid;

            return Task.FromResult<RefreshTokenRecord?>(replacement);
        }
    }

    public Task<RefreshTokenRecord?> FindRefreshTokenBySelector(string selector)
    {
        lock (_lock)
        {
            return Task.FromResult(
                _refreshTokenIdsBySelector.TryGetValue(selector, out var tokenUuid)
                    ? _refreshTokens[tokenUuid]
                    : null);
        }
    }

    public Task MarkRefreshTokenReuseDetected(Guid refreshTokenUuid, DateTimeOffset now)
    {
        lock (_lock)
        {
            var token = _refreshTokens[refreshTokenUuid];
            token.Status = RefreshTokenStatus.ReuseDetected;
            token.ReuseDetectedAt = now;

            if (_sessions.TryGetValue(token.SessionUuid, out var session))
            {
                session.RevokedAt ??= now;
                RevokeRefreshTokensForSession(session.SessionUuid, now);
            }
        }

        return Task.CompletedTask;
    }

    private void RevokeRefreshTokensForSession(Guid sessionUuid, DateTimeOffset now)
    {
        foreach (var token in _refreshTokens.Values.Where(token => token.SessionUuid == sessionUuid))
        {
            if (token.Status == RefreshTokenStatus.Active)
            {
                token.Status = RefreshTokenStatus.Revoked;
                token.RotatedAt = now;
            }
        }
    }

    private static string NormalizeProvider(string provider)
    {
        return provider.Trim().ToLowerInvariant();
    }

    private static RefreshTokenRecord NewRefreshTokenRecord(Guid sessionUuid, RefreshToken token)
    {
        return new RefreshTokenRecord
        {
            RefreshTokenUuid = Guid.NewGuid(),
            SessionUuid = sessionUuid,
            Selector = token.Selector,
            SecretHash = token.SecretHash,
            Status = RefreshTokenStatus.Active,
            IssuedAt = token.IssuedAt,
            ExpiresAt = token.ExpiresAt
        };
    }
}
