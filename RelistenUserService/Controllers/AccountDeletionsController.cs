using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Relisten.Accounts.Contracts.Deletion;
using RelistenUserService.Authentication;
using RelistenUserService.Authentication.Authorization;
using RelistenUserService.Configuration;
using RelistenUserService.Identity.Deletion;
using Temporalio.Exceptions;

namespace RelistenUserService.Controllers;

[ApiController]
[Route("v1/account-deletions")]
[Authorize(Policy = AuthenticationConstants.AccountManagePolicy)]
public sealed class AccountDeletionsController(
    AccountsOptions options,
    CurrentAccountContext currentAccount,
    AccountDeletionJobs jobs,
    AccountDeletionStarter starter,
    ILogger<AccountDeletionsController> logger
) : ControllerBase
{
    [HttpPost]
    [RequestSizeLimit(4096)]
    public async Task<IActionResult> Post(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] AccountDeletionRequest? request,
        CancellationToken cancellationToken
    )
    {
        if (!options.EnableAccountDeletion)
        {
            return NotFound();
        }

        var userId = currentAccount.User.Id;
        var jobId = await jobs.RequestAsync(userId, cancellationToken);
        if (jobId is null)
        {
            return Unauthorized();
        }

        // TODO: Exchange apple_authorization_code and revoke the resulting token within five
        // seconds for an account with an Apple identity. The configured Apple client is the
        // browser Services ID. Native codes need the matching App ID and signing credentials.

        try
        {
            await starter.StartAsync(new(jobId.Value, userId.ToString()), cancellationToken);
        }
        catch (Exception error)
            when (error is RpcException or InvalidOperationException or OperationCanceledException
                && !cancellationToken.IsCancellationRequested
            )
        {
            // Temporal may have accepted the start before the reply was lost. Leave the
            // committed job for a same-ID retry while the session remains valid.
            logger.LogWarning(
                error,
                "Could not confirm start of account deletion job {JobId}.",
                jobId
            );
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        return Accepted(new { state = "deleting" });
    }
}

public sealed record AccountDeletionRequest(string? AppleAuthorizationCode);
