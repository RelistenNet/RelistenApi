using Microsoft.EntityFrameworkCore;
using RelistenUserService.Configuration;
using RelistenUserService.Identity.Entities;
using RelistenUserService.Persistence;

namespace RelistenUserService.Authentication;

public sealed class IdentitySessionLifecycle(
    AccountsDbContext dbContext,
    SessionCredentialCodec credentialCodec,
    TimeProvider timeProvider,
    AccountsRuntimeConfiguration runtime)
{
    public static readonly TimeSpan AuthSsoLifetime = TimeSpan.FromDays(30);
    public static readonly TimeSpan WebSlidingLifetime = TimeSpan.FromDays(30);
    public static readonly TimeSpan WebAbsoluteLifetime = TimeSpan.FromDays(180);
    public static readonly TimeSpan TouchInterval = TimeSpan.FromHours(1);

    public async Task<IssuedIdentitySession> CreateAuthSsoAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var user = await LoadActiveUserAsync(userId, cancellationToken);
        var expiresAt = now + AuthSsoLifetime;
        return await CreateAsync(new IdentitySession
        {
            Id = Guid.CreateVersion7(now),
            UserId = user.Id,
            Purpose = IdentitySessionPurposes.AuthSso,
            SecurityVersion = user.SecurityVersion,
            AuthenticatedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
            LastSeenAt = now,
            SlidingExpiresAt = expiresAt,
            AbsoluteExpiresAt = expiresAt,
            Capabilities = IdentitySessionCapabilities.None
        }, cancellationToken);
    }

    public async Task<IssuedIdentitySession> CreateWebAsync(
        Guid userId,
        Guid authSsoSessionId,
        string webOrigin,
        CancellationToken cancellationToken)
    {
        if (!runtime.WebOrigins.Contains(webOrigin, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("The web origin is not configured for browser sessions.");
        }

        var now = timeProvider.GetUtcNow();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            cancellationToken);
        var parent = await dbContext.Sessions
            .FromSqlInterpolated($"""
                SELECT *
                FROM identity.sessions
                WHERE id = {authSsoSessionId}
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(cancellationToken);
        var user = await LoadActiveUserAsync(userId, cancellationToken);

        if (parent is null
            || parent.Purpose != IdentitySessionPurposes.AuthSso
            || parent.UserId != user.Id
            || parent.SecurityVersion != user.SecurityVersion
            || parent.RevokedAt is not null
            || parent.SlidingExpiresAt <= now
            || parent.AbsoluteExpiresAt <= now
            || parent.Capabilities != IdentitySessionCapabilities.None)
        {
            throw new InvalidOperationException("The auth SSO session cannot create a web session.");
        }

        var session = await CreateAsync(new IdentitySession
        {
            Id = Guid.CreateVersion7(now),
            UserId = user.Id,
            Purpose = IdentitySessionPurposes.Web,
            SecurityVersion = user.SecurityVersion,
            AuthenticatedAt = parent.AuthenticatedAt,
            CreatedAt = now,
            UpdatedAt = now,
            LastSeenAt = now,
            SlidingExpiresAt = now + WebSlidingLifetime,
            AbsoluteExpiresAt = now + WebAbsoluteLifetime,
            AuthSsoSessionId = parent.Id,
            WebOrigin = webOrigin,
            Capabilities = IdentitySessionCapabilities.AllWeb
        }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return session;
    }

    public async Task<AuthenticatedIdentitySession?> AuthenticateAsync(
        string? cookieValue,
        string expectedPurpose,
        CancellationToken cancellationToken)
    {
        if (!credentialCodec.TryParse(cookieValue, out var credential)
            || credential is null)
        {
            return null;
        }

        try
        {
            var now = timeProvider.GetUtcNow();
            var session = await dbContext.Sessions
                .AsNoTracking()
                .Include(candidate => candidate.User)
                .Include(candidate => candidate.AuthSsoSession)
                .SingleOrDefaultAsync(
                    candidate => candidate.Id == credential.SessionId,
                    cancellationToken);
            if (!IsValid(session, credential, expectedPurpose, now))
            {
                return null;
            }

            var touched = await TouchAsync(session!, now, cancellationToken);
            return new AuthenticatedIdentitySession(
                session!.Id,
                session.User,
                session.Purpose,
                session.AuthenticatedAt,
                session.AuthSsoSessionId,
                session.WebOrigin,
                session.Capabilities,
                touched,
                touched
                    ? Min(now + SlidingLifetime(session.Purpose), session.AbsoluteExpiresAt)
                    : session.SlidingExpiresAt);
        }
        finally
        {
            credential.Clear();
        }
    }

    public async Task<AuthenticatedIdentitySession?> ValidateAuthSsoBootstrapAsync(
        Guid sessionId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var session = await dbContext.Sessions
            .AsNoTracking()
            .Include(candidate => candidate.User)
            .SingleOrDefaultAsync(
                candidate => candidate.Id == sessionId && candidate.UserId == userId,
                cancellationToken);
        if (session is null
            || session.Purpose != IdentitySessionPurposes.AuthSso
            || session.RevokedAt is not null
            || session.SlidingExpiresAt <= now
            || session.AbsoluteExpiresAt <= now
            || session.User.Status != UserStatuses.Active
            || session.SecurityVersion != session.User.SecurityVersion
            || session.AuthSsoSessionId is not null
            || session.WebOrigin is not null
            || session.Capabilities != IdentitySessionCapabilities.None)
        {
            return null;
        }

        return new AuthenticatedIdentitySession(
            session.Id,
            session.User,
            session.Purpose,
            session.AuthenticatedAt,
            null,
            null,
            session.Capabilities,
            WasTouched: false,
            session.SlidingExpiresAt);
    }

    public async Task RevokeWebAndParentAsync(
        Guid webSessionId,
        CancellationToken cancellationToken)
    {
        var session = await dbContext.Sessions
            .AsNoTracking()
            .Where(candidate => candidate.Id == webSessionId)
            .Select(candidate => new
            {
                candidate.Purpose,
                candidate.AuthSsoSessionId
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (session is null || session.Purpose != IdentitySessionPurposes.Web)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            cancellationToken);
        var parent = await dbContext.Sessions
            .FromSqlInterpolated($"""
                SELECT *
                FROM identity.sessions
                WHERE id = {session.AuthSsoSessionId}
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(cancellationToken);
        if (parent is null || parent.Purpose != IdentitySessionPurposes.AuthSso)
        {
            await transaction.RollbackAsync(cancellationToken);
            return;
        }

        await dbContext.Sessions
            .Where(candidate =>
                candidate.Id == parent.Id
                || candidate.AuthSsoSessionId == parent.Id)
            .Where(candidate => candidate.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(candidate => candidate.RevokedAt, now)
                .SetProperty(candidate => candidate.UpdatedAt, now), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<IssuedIdentitySession> CreateAsync(
        IdentitySession session,
        CancellationToken cancellationToken)
    {
        var credential = credentialCodec.Issue(session.Id);
        session.ValidatorHash = credential.ValidatorHash;
        dbContext.Sessions.Add(session);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new IssuedIdentitySession(
            session.Id,
            session.UserId,
            session.Purpose,
            session.AuthenticatedAt,
            session.AuthSsoSessionId,
            session.WebOrigin,
            session.Capabilities,
            session.SlidingExpiresAt,
            credential.CookieValue);
    }

    private async Task<User> LoadActiveUserAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        var user = await dbContext.Users.SingleOrDefaultAsync(
            candidate => candidate.Id == userId,
            cancellationToken);
        if (user is null || user.Status != UserStatuses.Active)
        {
            throw new InvalidOperationException("The active user does not exist.");
        }

        return user;
    }

    private bool IsValid(
        IdentitySession? session,
        ParsedSessionCredential credential,
        string expectedPurpose,
        DateTimeOffset now)
    {
        if (session is null
            || session.Purpose != expectedPurpose
            || !credentialCodec.ValidatorMatches(
                credential.ValidatorHash,
                session.ValidatorHash)
            || session.RevokedAt is not null
            || session.SlidingExpiresAt <= now
            || session.AbsoluteExpiresAt <= now
            || session.User.Status != UserStatuses.Active
            || session.SecurityVersion != session.User.SecurityVersion)
        {
            return false;
        }

        return session.Purpose switch
        {
            IdentitySessionPurposes.AuthSso =>
                session.AuthSsoSessionId is null
                && session.WebOrigin is null
                && session.Capabilities == IdentitySessionCapabilities.None,
            IdentitySessionPurposes.Web =>
                session.AuthSsoSession is
                {
                    Purpose: IdentitySessionPurposes.AuthSso,
                    RevokedAt: null,
                    Capabilities: IdentitySessionCapabilities.None
                }
                && session.AuthSsoSession.UserId == session.UserId
                && session.AuthSsoSession.SecurityVersion == session.User.SecurityVersion
                && session.WebOrigin is not null
                && runtime.WebOrigins.Contains(session.WebOrigin, StringComparer.Ordinal)
                && session.Capabilities == IdentitySessionCapabilities.AllWeb,
            _ => false
        };
    }

    private async Task<bool> TouchAsync(
        IdentitySession session,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (session.LastSeenAt > now - TouchInterval)
        {
            return false;
        }

        var proposedExpiry = now + SlidingLifetime(session.Purpose);
        return await dbContext.Sessions
            .Where(candidate => candidate.Id == session.Id)
            .Where(candidate => candidate.RevokedAt == null)
            .Where(candidate => candidate.SlidingExpiresAt > now)
            .Where(candidate => candidate.AbsoluteExpiresAt > now)
            .Where(candidate => candidate.LastSeenAt <= now - TouchInterval)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(candidate => candidate.LastSeenAt, now)
                .SetProperty(candidate => candidate.UpdatedAt, now)
                .SetProperty(
                    candidate => candidate.SlidingExpiresAt,
                    candidate => candidate.AbsoluteExpiresAt < proposedExpiry
                        ? candidate.AbsoluteExpiresAt
                        : proposedExpiry), cancellationToken) == 1;
    }

    private static TimeSpan SlidingLifetime(string purpose) => purpose switch
    {
        IdentitySessionPurposes.AuthSso => AuthSsoLifetime,
        IdentitySessionPurposes.Web => WebSlidingLifetime,
        _ => throw new InvalidOperationException("The session purpose is not supported.")
    };

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) =>
        left <= right ? left : right;
}

public sealed class IssuedIdentitySession(
    Guid sessionId,
    Guid userId,
    string purpose,
    DateTimeOffset authenticatedAt,
    Guid? authSsoSessionId,
    string? webOrigin,
    IdentitySessionCapabilities capabilities,
    DateTimeOffset expiresAt,
    string cookieValue)
{
    public Guid SessionId { get; } = sessionId;
    public Guid UserId { get; } = userId;
    public string Purpose { get; } = purpose;
    public DateTimeOffset AuthenticatedAt { get; } = authenticatedAt;
    public Guid? AuthSsoSessionId { get; } = authSsoSessionId;
    public string? WebOrigin { get; } = webOrigin;
    public IdentitySessionCapabilities Capabilities { get; } = capabilities;
    public DateTimeOffset ExpiresAt { get; } = expiresAt;
    public string CookieValue { get; } = cookieValue;
}

public sealed record AuthenticatedIdentitySession(
    Guid SessionId,
    User User,
    string Purpose,
    DateTimeOffset AuthenticatedAt,
    Guid? AuthSsoSessionId,
    string? WebOrigin,
    IdentitySessionCapabilities Capabilities,
    bool WasTouched,
    DateTimeOffset ExpiresAt);
