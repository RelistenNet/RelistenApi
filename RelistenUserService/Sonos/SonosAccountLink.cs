using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using RelistenUserService.Authentication;
using RelistenUserService.Identity.Entities;
using RelistenUserService.Persistence;

namespace RelistenUserService.Sonos;

public sealed class SonosAccountLink(
    AccountsDbContext db,
    SonosProtocol protocol,
    IDataProtectionProvider protection,
    AdvisoryLockService locks,
    TimeProvider clock
)
{
    private readonly IDataProtector _protector = protection.CreateProtector(
        "Relisten.Sonos.ControlGrant.v1"
    );

    internal async Task<string> BeginAsync(
        SonosConfiguration config,
        UserId user,
        SessionId session,
        bool returnToMobile,
        CancellationToken ct
    )
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await locks.LockAsync($"sonos-grant:{user}", ct);
        var now = clock.GetUtcNow();
        await db.Set<SonosOAuthState>()
            .Where(row => row.UserId == user && row.ExpiresAt <= now)
            .ExecuteDeleteAsync(ct);
        var state = SonosProtocol.NewSecret();
        db.Add(
            new SonosOAuthState
            {
                Hash = SonosProtocol.Hash(state),
                UserId = user,
                SessionId = session,
                ReturnToMobile = returnToMobile,
                ExpiresAt = now.AddMinutes(10),
            }
        );
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return SonosProtocol.AuthorizeUrl(config, state);
    }

    internal async Task<SonosLinkResult?> CompleteAsync(
        SonosConfiguration config,
        string state,
        string? code,
        CancellationToken ct
    )
    {
        var hash = SonosProtocol.Hash(state);
        var owner = await db.Set<SonosOAuthState>()
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.Hash == hash, ct);
        if (owner is null)
        {
            return null;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await locks.LockAsync($"sonos-grant:{owner.UserId}", ct);
        var now = clock.GetUtcNow();
        if (
            await db.Set<SonosOAuthState>()
                .Where(row => row.Hash == hash && row.ExpiresAt > now)
                .ExecuteDeleteAsync(ct) != 1
        )
        {
            return null;
        }

        if (
            string.IsNullOrEmpty(code)
            || !await SessionActiveAsync(owner.UserId, owner.SessionId, ct)
        )
        {
            await transaction.CommitAsync(ct);
            return new SonosLinkResult(false, owner.ReturnToMobile);
        }

        if (
            await db.Set<SonosQueue>()
                .AnyAsync(
                    queue =>
                        queue.UserId == owner.UserId
                        && queue.Status == "dispatching"
                        && queue.ExpiresAt > now,
                    ct
                )
        )
        {
            await transaction.CommitAsync(ct);
            return new SonosLinkResult(false, owner.ReturnToMobile);
        }

        StoredSonosToken stored;
        try
        {
            var token = await protocol.TokenAsync(
                config,
                new()
                {
                    ["grant_type"] = "authorization_code",
                    ["code"] = code,
                    ["redirect_uri"] = config.RedirectUri.AbsoluteUri,
                },
                ct
            );
            stored = ValidateToken(token, null);
        }
        catch (Exception error)
            when (!ct.IsCancellationRequested
                && error
                    is SonosRequestException
                        or HttpRequestException
                        or JsonException
                        or OperationCanceledException
            )
        {
            await transaction.CommitAsync(ct);
            return new SonosLinkResult(false, owner.ReturnToMobile);
        }

        if (!await SessionActiveAsync(owner.UserId, owner.SessionId, ct))
        {
            await transaction.CommitAsync(ct);
            return new SonosLinkResult(false, owner.ReturnToMobile);
        }

        var grant = await db.Set<SonosGrant>().FindAsync([owner.UserId], ct);
        if (grant is null)
        {
            grant = new SonosGrant { UserId = owner.UserId };
            db.Add(grant);
        }

        // A newly linked household must never inherit a previous grant's active queues.
        await db.Set<SonosQueue>()
            .Where(row => row.UserId == owner.UserId && row.Status != "revoked")
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.Status, "revoked"), ct);
        SaveToken(grant, stored);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new SonosLinkResult(true, owner.ReturnToMobile);
    }

    internal async Task DisconnectAsync(UserId user, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await locks.LockAsync($"sonos-grant:{user}", ct);
        await db.Set<SonosOAuthState>().Where(row => row.UserId == user).ExecuteDeleteAsync(ct);
        await db.Set<SonosGrant>().Where(row => row.UserId == user).ExecuteDeleteAsync(ct);
        await db.Set<SonosQueue>()
            .Where(row => row.UserId == user)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.Status, "revoked"), ct);
        await transaction.CommitAsync(ct);
    }

    internal Task<bool> IsLinkedAsync(UserId user, CancellationToken ct) =>
        db.Set<SonosGrant>().AnyAsync(row => row.UserId == user, ct);

    internal async Task<SonosAccess?> AccessAsync(
        SonosConfiguration config,
        UserId user,
        CancellationToken ct
    )
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await locks.LockAsync($"sonos-grant:{user}", ct);
        var grant = await db.Set<SonosGrant>().FindAsync([user], ct);
        if (grant is null)
        {
            return null;
        }

        var stored = JsonSerializer.Deserialize<StoredSonosToken>(
            _protector.Unprotect(grant.ProtectedToken)
        )!;
        if (stored.ExpiresAt <= clock.GetUtcNow().AddMinutes(1))
        {
            try
            {
                var token = await protocol.TokenAsync(
                    config,
                    new()
                    {
                        ["grant_type"] = "refresh_token",
                        ["refresh_token"] = stored.RefreshToken,
                    },
                    ct
                );
                stored = ValidateToken(token, stored.RefreshToken);
                SaveToken(grant, stored);
            }
            catch (SonosRequestException exception)
                when (exception.Status == HttpStatusCode.BadRequest)
            {
                db.Remove(grant);
                await db.Set<SonosQueue>()
                    .Where(row => row.UserId == user)
                    .ExecuteUpdateAsync(set => set.SetProperty(row => row.Status, "revoked"), ct);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return null;
            }

            await db.SaveChangesAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return new SonosAccess(stored.AccessToken, grant.ProtectedToken);
    }

    internal Task<bool> SessionActiveAsync(UserId user, SessionId session, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return db.NativeSessions.AnyAsync(
            row =>
                row.UserId == user
                && row.Id == session
                && row.RevokedAt == null
                && row.AbsoluteExpiresAt > now
                && row.User.Status == UserStatuses.Active
                && row.User.SecurityVersion == row.SecurityVersion
                && row.LastUsedAt > now - AuthenticationConstants.NativeSessionInactivityLimit,
            ct
        );
    }

    private StoredSonosToken ValidateToken(SonosToken token, string? previousRefresh)
    {
        var refresh = token.RefreshToken ?? previousRefresh;
        if (
            string.IsNullOrWhiteSpace(refresh)
            || string.IsNullOrWhiteSpace(token.AccessToken)
            || token.ExpiresIn <= 0
        )
        {
            throw new JsonException("Sonos returned incomplete credentials.");
        }

        return new StoredSonosToken(
            token.AccessToken,
            refresh,
            clock.GetUtcNow().AddSeconds(token.ExpiresIn)
        );
    }

    private void SaveToken(SonosGrant grant, StoredSonosToken stored) =>
        grant.ProtectedToken = _protector.Protect(JsonSerializer.Serialize(stored));
}

internal sealed record SonosAccess(string Token, string GrantVersion);

internal sealed record SonosLinkResult(bool Connected, bool ReturnToMobile);
