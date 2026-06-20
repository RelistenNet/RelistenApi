using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Relisten.UserApi.Configuration;
using Relisten.UserApi.Models;

namespace Relisten.UserApi.Services;

public sealed partial class UserAuthService
{
    private readonly IAuthProviderVerifier _providerVerifier;
    private readonly IUserAuthStore _authStore;
    private readonly AccessTokenService _accessTokenService;
    private readonly RefreshTokenService _refreshTokenService;
    private readonly UserAuthOptions _options;

    public UserAuthService(
        IAuthProviderVerifier providerVerifier,
        IUserAuthStore authStore,
        AccessTokenService accessTokenService,
        RefreshTokenService refreshTokenService,
        IOptions<UserAuthOptions> options)
    {
        _providerVerifier = providerVerifier;
        _authStore = authStore;
        _accessTokenService = accessTokenService;
        _refreshTokenService = refreshTokenService;
        _options = options.Value;
    }

    public async Task<AuthTokenResponse> SignInWithProvider(string provider, ProviderSignInRequest request)
    {
        var now = DateTimeOffset.UtcNow;
        var normalizedProvider = NormalizeProvider(provider);
        if (!_options.AllowedProviders.Contains(normalizedProvider, StringComparer.OrdinalIgnoreCase))
        {
            throw new UserAuthException("provider_not_supported");
        }

        var identity = await _providerVerifier.Verify(normalizedProvider, request.ProviderToken, request.Nonce);
        if (!string.Equals(normalizedProvider, identity.Provider, StringComparison.OrdinalIgnoreCase))
        {
            throw new UserAuthException("provider_mismatch");
        }

        var existing = await _authStore.FindUserByProvider(identity.Provider, identity.ProviderSubject);
        var user = existing?.User ?? await CreateUser(identity, request, now);
        var session = await _authStore.CreateSession(
            user.UserUuid,
            new DeviceDescriptor(request.DeviceId, request.DeviceName, request.Platform),
            now);

        return await IssueTokenResponse(user, session, now);
    }

    public async Task<UserSessionResponse> Reauthenticate(
        Guid userUuid,
        Guid? sessionUuid,
        string provider,
        ProviderReauthenticationRequest request)
    {
        if (!sessionUuid.HasValue)
        {
            throw new UserAuthException("session_required");
        }

        var normalizedProvider = NormalizeProvider(provider);
        if (!_options.AllowedProviders.Contains(normalizedProvider, StringComparer.OrdinalIgnoreCase))
        {
            throw new UserAuthException("provider_not_supported");
        }

        var identity = await _providerVerifier.Verify(normalizedProvider, request.ProviderToken, request.Nonce);
        if (!string.Equals(normalizedProvider, identity.Provider, StringComparison.OrdinalIgnoreCase))
        {
            throw new UserAuthException("provider_mismatch");
        }

        var linkedUser = await _authStore.FindUserByProvider(identity.Provider, identity.ProviderSubject);
        if (linkedUser == null || linkedUser.Value.User.UserUuid != userUuid)
        {
            throw new UserAuthException("provider_not_linked");
        }

        var now = DateTimeOffset.UtcNow;
        await _authStore.MarkSessionReauthenticated(userUuid, sessionUuid.Value, now);
        var session = await _authStore.GetSession(sessionUuid.Value)
            ?? throw new UserAuthException("session_revoked");
        if (session.UserUuid != userUuid || session.RevokedAt != null)
        {
            throw new UserAuthException("session_revoked");
        }

        return ToSessionResponse(session);
    }

    public async Task<AuthTokenResponse> SignInDevelopmentUser(DevelopmentSessionRequest request)
    {
        var now = DateTimeOffset.UtcNow;
        var username = NormalizeUsername(request.Username);
        var existing = await _authStore.FindUserByProvider(DevelopmentProvider, username);
        var user = existing?.User ?? (await _authStore.CreateUserWithProvider(
            DevelopmentProvider,
            username,
            username,
            DisplayNameOrUsername(request.DisplayName, username),
            now)).User;
        var session = await _authStore.CreateSession(
            user.UserUuid,
            new DeviceDescriptor(request.DeviceId, request.DeviceName, request.Platform),
            now);

        return await IssueTokenResponse(user, session, now);
    }

    public async Task<AuthTokenResponse> Refresh(string refreshToken)
    {
        var now = DateTimeOffset.UtcNow;
        var parsed = _refreshTokenService.Parse(refreshToken);
        var token = await _authStore.FindRefreshTokenBySelector(parsed.Selector)
            ?? throw new UserAuthException("invalid_refresh_token");

        if (!_refreshTokenService.Verify(parsed.Secret, token.SecretHash))
        {
            throw new UserAuthException("invalid_refresh_token");
        }

        if (token.Status != RefreshTokenStatus.Active)
        {
            if (token.Status == RefreshTokenStatus.Rotated)
            {
                await _authStore.MarkRefreshTokenReuseDetected(token.RefreshTokenUuid, now);
                throw new UserAuthException("refresh_token_reuse_detected");
            }

            throw new UserAuthException("invalid_refresh_token");
        }

        if (token.ExpiresAt <= now)
        {
            throw new UserAuthException("invalid_refresh_token");
        }

        var session = await _authStore.GetSession(token.SessionUuid)
            ?? throw new UserAuthException("invalid_refresh_token");
        if (session.RevokedAt != null)
        {
            throw new UserAuthException("session_revoked");
        }

        var user = await _authStore.FindUser(session.UserUuid)
            ?? throw new UserAuthException("invalid_refresh_token");
        var replacementToken = _refreshTokenService.Issue(now);
        var replacementRecord = await _authStore.RotateRefreshToken(
            token.RefreshTokenUuid,
            session.SessionUuid,
            replacementToken,
            now);
        if (replacementRecord == null)
        {
            await _authStore.MarkRefreshTokenReuseDetected(token.RefreshTokenUuid, now);
            throw new UserAuthException("refresh_token_reuse_detected");
        }

        await _authStore.TouchSession(session.SessionUuid, now);

        return BuildTokenResponse(user, session, replacementToken.Plaintext, replacementToken.ExpiresAt, now);
    }

    public async Task Logout(string refreshToken)
    {
        var now = DateTimeOffset.UtcNow;
        var parsed = _refreshTokenService.Parse(refreshToken);
        var token = await _authStore.FindRefreshTokenBySelector(parsed.Selector)
            ?? throw new UserAuthException("invalid_refresh_token");

        if (!_refreshTokenService.Verify(parsed.Secret, token.SecretHash))
        {
            throw new UserAuthException("invalid_refresh_token");
        }

        await _authStore.RevokeSessionByRefreshToken(token.RefreshTokenUuid, now);
    }

    public async Task<IReadOnlyList<UserSessionResponse>> ListSessions(Guid userUuid)
    {
        var sessions = await _authStore.ListSessions(userUuid);
        return sessions.Select(ToSessionResponse).ToList();
    }

    public Task RevokeSession(Guid userUuid, Guid sessionUuid)
    {
        return _authStore.RevokeSession(userUuid, sessionUuid, DateTimeOffset.UtcNow);
    }

    public async Task RequireRecentReauthentication(Guid userUuid, Guid? sessionUuid)
    {
        if (!sessionUuid.HasValue)
        {
            throw new UserAuthException("recent_reauthentication_required");
        }

        var session = await _authStore.GetSession(sessionUuid.Value);
        var window = TimeSpan.FromMinutes(Math.Max(1, _options.RecentReauthenticationWindowMinutes));
        var cutoff = DateTimeOffset.UtcNow.Subtract(window);
        if (session == null ||
            session.UserUuid != userUuid ||
            session.RevokedAt != null ||
            session.ReauthenticatedAt == null ||
            session.ReauthenticatedAt < cutoff)
        {
            throw new UserAuthException("recent_reauthentication_required");
        }
    }

    private async Task<UserAccount> CreateUser(
        ProviderIdentity identity,
        ProviderSignInRequest request,
        DateTimeOffset now)
    {
        var username = NormalizeUsername(request.Username);

        return (await _authStore.CreateUserWithProvider(
            identity.Provider,
            identity.ProviderSubject,
            username,
            DisplayNameOrUsername(request.DisplayName, username),
            now)).User;
    }

    private async Task<AuthTokenResponse> IssueTokenResponse(
        UserAccount user,
        UserSession session,
        DateTimeOffset now)
    {
        var refreshToken = await AddRefreshToken(session.SessionUuid, now);
        return BuildTokenResponse(user, session, refreshToken.Token.Plaintext, refreshToken.Token.ExpiresAt, now);
    }

    private async Task<(RefreshToken Token, RefreshTokenRecord Record)> AddRefreshToken(
        Guid sessionUuid,
        DateTimeOffset now)
    {
        var token = _refreshTokenService.Issue(now);
        var record = await _authStore.AddRefreshToken(sessionUuid, token);
        return (token, record);
    }

    private AuthTokenResponse BuildTokenResponse(
        UserAccount user,
        UserSession session,
        string refreshToken,
        DateTimeOffset refreshTokenExpiresAt,
        DateTimeOffset now)
    {
        var accessToken = _accessTokenService.Issue(user, session, now);
        return new AuthTokenResponse
        {
            AccessToken = accessToken.Plaintext,
            AccessTokenExpiresAt = accessToken.ExpiresAt,
            RefreshToken = refreshToken,
            RefreshTokenExpiresAt = refreshTokenExpiresAt,
            User = new CurrentUserResponse
            {
                UserUuid = user.UserUuid,
                DisplayName = user.DisplayName,
                Username = user.Username,
                ScopeId = $"user:{user.UserUuid}"
            },
            Session = ToSessionResponse(session)
        };
    }

    private static UserSessionResponse ToSessionResponse(UserSession session)
    {
        return new UserSessionResponse
        {
            SessionUuid = session.SessionUuid,
            DeviceId = session.DeviceId,
            DeviceName = session.DeviceName,
            Platform = session.Platform,
            LastUsedAt = session.LastUsedAt,
            CreatedAt = session.CreatedAt,
            ReauthenticatedAt = session.ReauthenticatedAt,
            RevokedAt = session.RevokedAt
        };
    }

    private static string NormalizeUsername(string? username)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            throw new UserAuthException("username_required");
        }

        var normalized = username.Trim();
        if (!UsernamePattern().IsMatch(normalized))
        {
            throw new UserAuthException("invalid_username");
        }

        return normalized;
    }

    private static string NormalizeProvider(string provider)
    {
        return string.IsNullOrWhiteSpace(provider)
            ? string.Empty
            : provider.Trim().ToLowerInvariant();
    }

    private static string DisplayNameOrUsername(string? displayName, string username)
    {
        return string.IsNullOrWhiteSpace(displayName)
            ? username
            : displayName.Trim();
    }

    private const string DevelopmentProvider = "development";

    [GeneratedRegex("^[A-Za-z0-9_]{3,30}$")]
    private static partial Regex UsernamePattern();
}
