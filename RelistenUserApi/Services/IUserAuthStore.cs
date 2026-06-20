using Relisten.UserApi.Models;

namespace Relisten.UserApi.Services;

public interface IUserAuthStore
{
    Task<(UserAccount User, UserAuthMethod AuthMethod)?> FindUserByProvider(
        string provider,
        string providerSubject);

    Task<(UserAccount User, UserAuthMethod AuthMethod)> CreateUserWithProvider(
        string provider,
        string providerSubject,
        string username,
        string displayName,
        DateTimeOffset now);

    Task<UserAccount?> FindUser(Guid userUuid);
    Task<UserSession> CreateSession(Guid userUuid, DeviceDescriptor device, DateTimeOffset now);
    Task<UserSession?> GetSession(Guid sessionUuid);
    Task<IReadOnlyList<UserSession>> ListSessions(Guid userUuid);
    Task RevokeSession(Guid userUuid, Guid sessionUuid, DateTimeOffset now);
    Task RevokeSessionByRefreshToken(Guid refreshTokenUuid, DateTimeOffset now);
    Task TouchSession(Guid sessionUuid, DateTimeOffset now);
    Task MarkSessionReauthenticated(Guid userUuid, Guid sessionUuid, DateTimeOffset now);
    Task<RefreshTokenRecord> AddRefreshToken(Guid sessionUuid, RefreshToken token);
    Task<RefreshTokenRecord?> RotateRefreshToken(
        Guid currentRefreshTokenUuid,
        Guid sessionUuid,
        RefreshToken replacementToken,
        DateTimeOffset now);
    Task<RefreshTokenRecord?> FindRefreshTokenBySelector(string selector);
    Task MarkRefreshTokenReuseDetected(Guid refreshTokenUuid, DateTimeOffset now);
}

public sealed record DeviceDescriptor(string DeviceId, string? DeviceName, string Platform);
