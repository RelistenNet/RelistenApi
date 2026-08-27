using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Relisten.Accounts.Contracts.Library;
using RelistenUserService.Authentication;
using RelistenUserService.Library;

namespace RelistenUserService.Controllers;

[ApiController]
[Route("api/user/v1/library")]
public sealed class BrowserLibraryController(
    CurrentAccountContext currentAccount,
    FavoriteMutationService mutationService,
    LibraryReadService readService)
    : FavoriteLibraryControllerBase(currentAccount, mutationService, readService)
{
    [HttpGet("snapshot")]
    [Authorize(Policy = AuthenticationConstants.BrowserLibraryReadPolicy)]
    public Task<ActionResult<FavoriteLibrarySnapshot>> Snapshot(
        CancellationToken cancellationToken) =>
        GetSnapshotAsync(cancellationToken);

    [HttpGet("changes")]
    [Authorize(Policy = AuthenticationConstants.BrowserLibraryReadPolicy)]
    public Task<ActionResult<FavoriteLibraryChanges>> Changes(
        [FromQuery] string? after,
        CancellationToken cancellationToken) =>
        GetChangesAsync(after, "/api/user/v1/library/snapshot", cancellationToken);

    [HttpPost("favorite-mutations:batch")]
    [Authorize(Policy = AuthenticationConstants.BrowserFavoriteMutationPolicy)]
    [ServiceFilter<BrowserMutationProtectionFilter>]
    [RequestSizeLimit(256 * 1024)]
    public Task<ActionResult<FavoriteMutationBatchResponse>> MutateFavorites(
        [FromBody] FavoriteMutationBatchRequest? request,
        CancellationToken cancellationToken) =>
        MutateFavoritesAsync(request, cancellationToken);
}
