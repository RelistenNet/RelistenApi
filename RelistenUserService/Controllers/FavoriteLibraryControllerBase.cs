using Microsoft.AspNetCore.Mvc;
using Relisten.Accounts.Contracts.Errors;
using Relisten.Accounts.Contracts.Library;
using RelistenUserService.Authentication;
using RelistenUserService.Library;

namespace RelistenUserService.Controllers;

public abstract class FavoriteLibraryControllerBase(
    CurrentAccountContext currentAccount,
    FavoriteMutationService mutationService,
    LibraryReadService readService)
    : ControllerBase
{
    protected async Task<ActionResult<FavoriteLibrarySnapshot>> GetSnapshotAsync(
        CancellationToken cancellationToken) =>
        Ok(await readService.GetSnapshotAsync(currentAccount.User.Id, cancellationToken));

    protected async Task<ActionResult<FavoriteLibraryChanges>> GetChangesAsync(
        string? after,
        string snapshotUrl,
        CancellationToken cancellationToken)
    {
        var execution = await readService.GetChangesAsync(
            currentAccount.User.Id,
            after,
            cancellationToken);
        return execution.CursorExpired
            ? LibraryProblem(
                StatusCodes.Status410Gone,
                LibraryErrorCodes.SyncCursorExpired,
                "The library cursor is invalid or no longer retained. Fetch a new snapshot.",
                new() { ["snapshot_url"] = snapshotUrl })
            : Ok(execution.Response!);
    }

    protected async Task<ActionResult<FavoriteMutationBatchResponse>> MutateFavoritesAsync(
        FavoriteMutationBatchRequest? request,
        CancellationToken cancellationToken)
    {
        if (!FavoriteMutationRequestValidator.TryValidate(
                request,
                out var mutations,
                out var validationFailure))
        {
            return LibraryProblem(
                StatusCodes.Status422UnprocessableEntity,
                validationFailure!.Code,
                validationFailure.Detail);
        }

        var execution = await mutationService.ExecuteAsync(
            currentAccount.User.Id,
            mutations,
            cancellationToken);
        if (execution.Response is not null)
        {
            return Ok(execution.Response);
        }

        var failure = execution.Failure!;
        return failure.Kind switch
        {
            FavoriteMutationFailureKind.IdempotencyConflict => LibraryProblem(
                StatusCodes.Status409Conflict,
                AccountErrorCodes.IdempotencyConflict,
                failure.Detail,
                new() { ["conflicting_mutation_uuids"] = failure.ConflictingUuids }),
            FavoriteMutationFailureKind.FavoriteUuidConflict => LibraryProblem(
                StatusCodes.Status409Conflict,
                LibraryErrorCodes.FavoriteUuidConflict,
                failure.Detail,
                new() { ["conflicting_favorite_uuids"] = failure.ConflictingUuids }),
            FavoriteMutationFailureKind.QuotaExceeded => LibraryProblem(
                StatusCodes.Status422UnprocessableEntity,
                LibraryErrorCodes.QuotaExceeded,
                failure.Detail),
            _ => throw new InvalidOperationException("Unknown favorite mutation failure.")
        };
    }

    private ObjectResult LibraryProblem(
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
}
