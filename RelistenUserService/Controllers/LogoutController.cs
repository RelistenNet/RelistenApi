using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using RelistenUserService.Authentication;
using RelistenUserService.Persistence;

namespace RelistenUserService.Controllers;

[ApiController]
public sealed class LogoutController(
    AccountsDbContext dbContext,
    CurrentAccountContext currentAccount,
    IOpenIddictAuthorizationManager authorizationManager,
    TimeProvider timeProvider)
    : ControllerBase
{
    [HttpPost("v1/logout")]
    [Authorize(Policy = AuthenticationConstants.AccountManagePolicy)]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var session = await dbContext.NativeSessions.SingleAsync(
            item => item.Id == currentAccount.NativeSession.Id,
            cancellationToken);
        var now = timeProvider.GetUtcNow();
        session.RevokedAt ??= now;
        session.UpdatedAt = now;

        var authorization = await authorizationManager.FindByIdAsync(
            session.AuthorizationId.ToString("D"),
            cancellationToken);
        if (authorization is not null)
        {
            await authorizationManager.TryRevokeAsync(authorization, cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return NoContent();
    }
}
