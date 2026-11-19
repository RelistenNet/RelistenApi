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
        var permitted = runtime.Mcp is not null;
        var clientIds = McpOAuth.Clients.Select(client => client.ClientId).ToArray();
        var statuses = await (
            from session in Sessions()
            join grant in database.Set<OpenIddictEntityFrameworkCoreAuthorization<Guid>>()
                on session.AuthorizationId equals grant.Id
            where grant.Status == Statuses.Valid && clientIds.Contains(session.ClientId)
            select new
            {
                session.ClientId,
                Active = permitted
                    && session.AbsoluteExpiresAt > now
                    && session.LastUsedAt > cutoff
                    && session.SecurityVersion == account.User.SecurityVersion,
            }
        )
            .GroupBy(session => session.ClientId)
            .Select(sessions => new
            {
                ClientId = sessions.Key,
                Active = sessions.Any(session => session.Active),
            })
            .ToDictionaryAsync(
                status => status.ClientId,
                status => status.Active,
                cancellationToken
            );

        return Ok(
            new
            {
                clients = McpOAuth
                    .Clients.Where(client => statuses.ContainsKey(client.ClientId))
                    .Select(client => new
                    {
                        id = client.ConnectionId,
                        name = client.DisplayName,
                        // Expired sessions require fresh sign-in and consent.
                        state = statuses[client.ClientId] ? "connected" : "expired",
                    }),
            }
        );
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Disconnect(string id, CancellationToken cancellationToken)
    {
        var client = McpOAuth.Clients.FirstOrDefault(candidate => candidate.ConnectionId == id);
        if (client is null)
        {
            return NotFound();
        }

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
        foreach (
            var session in await Sessions()
                .Where(session => session.ClientId == client.ClientId)
                .ToListAsync(cancellationToken)
        )
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
            session.UserId == account.User.Id && session.RevokedAt == null
        );
}
