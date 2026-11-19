using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using OpenIddict.EntityFrameworkCore.Models;
using RelistenUserService.Authentication;
using RelistenUserService.Authentication.Authorization;
using RelistenUserService.Authentication.OpenIdConnect;
using RelistenUserService.Configuration;
using RelistenUserService.Identity.Entities;
using RelistenUserService.Persistence;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace RelistenUserService.Controllers;

[ApiController]
[Route("v1/connected-clients")]
[Authorize(Policy = AuthenticationConstants.AccountManagePolicy)]
public sealed class ConnectedClientsController(
    AccountsDbContext database,
    CurrentAccountContext account,
    IOpenIddictAuthorizationManager authorizations,
    AccountsRuntimeConfiguration runtime,
    TimeProvider clock
) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var cutoff = now - AuthenticationConstants.NativeSessionInactivityLimit;
        var permitted = runtime.Mcp?.Allows(account.User.Id) == true;
        var status = await (
            from session in Sessions()
            join grant in database.Set<OpenIddictEntityFrameworkCoreAuthorization<Guid>>()
                on session.AuthorizationId equals grant.Id
            where grant.Status == Statuses.Valid
            select permitted
                && session.AbsoluteExpiresAt > now
                && session.LastUsedAt > cutoff
                && session.SecurityVersion == account.User.SecurityVersion
        )
            .OrderByDescending(active => active)
            .Select(active => (bool?)active)
            .FirstOrDefaultAsync(cancellationToken);

        return Ok(
            new
            {
                clients = status is { } active
                    ? new[]
                    {
                        new
                        {
                            id = "chatgpt",
                            name = "ChatGPT",
                            state = active ? "connected" : "consent_remembered",
                        },
                    }
                    : [],
            }
        );
    }

    [HttpDelete("chatgpt")]
    public async Task<IActionResult> Disconnect(CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(
            cancellationToken
        );
        // Grant issuance and refresh both hold FOR SHARE on this account.
        var user = await database
            .Users.FromSqlInterpolated(
                $"SELECT * FROM identity.users WHERE id = {account.User.Id.Value} AND status = {UserStatuses.Active} FOR UPDATE"
            )
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken);
        if (user is null)
        {
            return Unauthorized();
        }

        var now = clock.GetUtcNow();
        foreach (var session in await Sessions().ToListAsync(cancellationToken))
        {
            var grant = await authorizations.FindByIdAsync(
                session.AuthorizationId.ToString("D"),
                cancellationToken
            );
            if (grant is not null && !await authorizations.TryRevokeAsync(grant, cancellationToken))
            {
                return Conflict();
            }
            session.RevokedAt = now;
            session.UpdatedAt = now;
        }

        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return NoContent();
    }

    private IQueryable<NativeSession> Sessions() =>
        database.NativeSessions.Where(session =>
            session.UserId == account.User.Id
            && session.ClientId == McpOAuth.ClientId
            && session.RevokedAt == null
        );
}
