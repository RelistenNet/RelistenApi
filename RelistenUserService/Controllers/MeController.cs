using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Relisten.Accounts.Contracts.Accounts;
using Relisten.Accounts.Contracts.Errors;
using RelistenUserService.Authentication;
using RelistenUserService.Authentication.Authorization;
using RelistenUserService.Identity;
using RelistenUserService.Identity.Deletion;
using RelistenUserService.Identity.Entities;
using RelistenUserService.Identity.Usernames;

namespace RelistenUserService.Controllers;

[ApiController]
[Route("v1/me")]
public sealed class MeController(
    CurrentAccountContext currentAccount,
    UsernameCommandService usernameCommands,
    AppleTokenRevocation apple
) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = AuthenticationConstants.UserReadPolicy)]
    public async Task<ActionResult<AccountProfileResponse>> Get(CancellationToken cancellationToken)
    {
        var profile = await CreateProfileAsync(currentAccount.User, cancellationToken);
        SetEtag(profile.UsernameVersion);
        return Ok(profile);
    }

    [HttpPatch]
    [Authorize(Policy = AuthenticationConstants.AccountManagePolicy)]
    public async Task<ActionResult<AccountProfileResponse>> Patch(
        UpdateUsernameRequest request,
        CancellationToken cancellationToken
    )
    {
        if (request.ContractVersion != 1)
        {
            return AccountProblem(
                StatusCodes.Status422UnprocessableEntity,
                AccountErrorCodes.InvalidContractVersion,
                "The username command contract version is not supported."
            );
        }

        if (request.CommandId.IsEmpty)
        {
            return AccountProblem(
                StatusCodes.Status422UnprocessableEntity,
                AccountErrorCodes.InvalidCommandId,
                "command_id must be a command_ UUIDv7 ID."
            );
        }

        var result = await usernameCommands.ExecuteAsync(
            currentAccount.User.Id,
            request,
            cancellationToken
        );
        if (result.Status == UsernameCommandStatus.Success)
        {
            var profile = await CreateProfileAsync(result.User, cancellationToken);
            SetEtag(profile.UsernameVersion);
            return Ok(profile);
        }

        return result.Status switch
        {
            UsernameCommandStatus.IdempotencyConflict => AccountProblem(
                StatusCodes.Status409Conflict,
                AccountErrorCodes.IdempotencyConflict,
                "command_id was already used for another username command."
            ),
            UsernameCommandStatus.InvalidUsername => AccountProblem(
                StatusCodes.Status422UnprocessableEntity,
                AccountErrorCodes.InvalidUsername,
                "Use 3–30 ASCII letters, digits, or underscores and choose a non-reserved name."
            ),
            UsernameCommandStatus.UsernameChangeTooSoon => AccountProblem(
                StatusCodes.Status409Conflict,
                AccountErrorCodes.UsernameChangeTooSoon,
                "The username may be changed once every 30 days.",
                new() { ["retry_at"] = result.RetryAt }
            ),
            UsernameCommandStatus.UsernameUnavailable => AccountProblem(
                StatusCodes.Status409Conflict,
                AccountErrorCodes.UsernameUnavailable,
                "The username is not available."
            ),
            UsernameCommandStatus.UsernameVersionStale => AccountProblem(
                StatusCodes.Status409Conflict,
                AccountErrorCodes.UsernameVersionStale,
                "The account has a newer username version.",
                new()
                {
                    ["current_profile"] = await CreateProfileAsync(result.User, cancellationToken),
                }
            ),
            _ => throw new InvalidOperationException("Unknown username command result."),
        };
    }

    private async Task<AccountProfileResponse> CreateProfileAsync(
        User user,
        CancellationToken cancellationToken
    ) =>
        AccountProfileFactory.Create(
            user,
            currentAccount.IsNative ? currentAccount.NativeSessionId : null
        ) with
        {
            AppleNativeRevocationAvailable =
                currentAccount.IsNative
                && await apple.CanRevokeNativeAsync(user.Id, cancellationToken),
        };

    private ObjectResult AccountProblem(
        int status,
        string code,
        string detail,
        Dictionary<string, object?>? extensions = null
    )
    {
        extensions ??= [];
        extensions["code"] = code;
        return Problem(statusCode: status, title: code, detail: detail, extensions: extensions);
    }

    private void SetEtag(long usernameVersion) =>
        Response.Headers.ETag = $"W/\"username-v{usernameVersion}\"";
}
