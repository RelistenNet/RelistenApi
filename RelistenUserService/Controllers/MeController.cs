using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Relisten.Accounts.Contracts.Accounts;
using Relisten.Accounts.Contracts.Errors;
using RelistenUserService.Authentication;
using RelistenUserService.Identity;
using RelistenUserService.Identity.Usernames;

namespace RelistenUserService.Controllers;

[ApiController]
[Route("v1/me")]
public sealed class MeController(
    CurrentAccountContext currentAccount,
    UsernameCommandService usernameCommands)
    : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = AuthenticationConstants.UserReadPolicy)]
    public ActionResult<AccountProfileResponse> Get()
    {
        var profile = CreateProfile();
        SetEtag(profile.UsernameVersion);
        return Ok(profile);
    }

    [HttpPatch]
    [Authorize(Policy = AuthenticationConstants.AccountManagePolicy)]
    public async Task<ActionResult<AccountProfileResponse>> Patch(
        UpdateUsernameRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ContractVersion != 1)
        {
            return AccountProblem(
                StatusCodes.Status422UnprocessableEntity,
                AccountErrorCodes.InvalidContractVersion,
                "The username command contract version is not supported.");
        }

        if (request.ClientCommandUuid == Guid.Empty || request.ClientCommandUuid.Version != 7)
        {
            return AccountProblem(
                StatusCodes.Status422UnprocessableEntity,
                AccountErrorCodes.InvalidCommandUuid,
                "client_command_uuid must be a UUIDv7 value.");
        }

        var result = await usernameCommands.ExecuteAsync(
            currentAccount.User.Id,
            request,
            cancellationToken);
        var profile = AccountProfileFactory.Create(result.User, currentAccount.NativeSession.Id);
        if (result.Status == UsernameCommandStatus.Success)
        {
            SetEtag(profile.UsernameVersion);
            return Ok(profile);
        }

        return result.Status switch
        {
            UsernameCommandStatus.IdempotencyConflict => AccountProblem(
                StatusCodes.Status409Conflict,
                AccountErrorCodes.IdempotencyConflict,
                "client_command_uuid was already used for another username command."),
            UsernameCommandStatus.InvalidUsername => AccountProblem(
                StatusCodes.Status422UnprocessableEntity,
                AccountErrorCodes.InvalidUsername,
                "Use 3–30 ASCII letters, digits, or underscores and choose a non-reserved name."),
            UsernameCommandStatus.UsernameChangeTooSoon => AccountProblem(
                StatusCodes.Status409Conflict,
                AccountErrorCodes.UsernameChangeTooSoon,
                "The username may be changed once every 30 days.",
                new() { ["retry_at"] = result.RetryAt }),
            UsernameCommandStatus.UsernameUnavailable => AccountProblem(
                StatusCodes.Status409Conflict,
                AccountErrorCodes.UsernameUnavailable,
                "The username is not available."),
            UsernameCommandStatus.UsernameVersionStale => AccountProblem(
                StatusCodes.Status409Conflict,
                AccountErrorCodes.UsernameVersionStale,
                "The account has a newer username version.",
                new() { ["current_profile"] = profile }),
            _ => throw new InvalidOperationException("Unknown username command result.")
        };
    }

    private AccountProfileResponse CreateProfile() => AccountProfileFactory.Create(
        currentAccount.User,
        currentAccount.NativeSession.Id);

    private ObjectResult AccountProblem(
        int status,
        string code,
        string detail,
        Dictionary<string, object?>? extensions = null)
    {
        extensions ??= [];
        extensions["code"] = code;
        return Problem(
            statusCode: status,
            title: code,
            detail: detail,
            extensions: extensions);
    }

    private void SetEtag(long usernameVersion) =>
        Response.Headers.ETag = $"W/\"username-v{usernameVersion}\"";
}
