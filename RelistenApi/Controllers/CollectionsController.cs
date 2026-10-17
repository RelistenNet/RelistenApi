using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Relisten.Api.Models;
using Relisten.Api.Models.Api;
using Relisten.Data;

namespace Relisten.Controllers;

/// <summary>
/// Browses published collections without changing performer or recording ownership.
/// Responses use live catalog joins; collection membership is maintained by operators.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/v3/collections")]
[Produces("application/json")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class CollectionsController(CollectionService collections) : ControllerBase
{
    /// <summary>Lists published collections in stable slug order. Limits are 1–200; cursors are resource-specific.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(CollectionPage<RelistenCollection>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public Task<IActionResult> List(
        [FromQuery] int limit = 50,
        [FromQuery] string? cursor = null
    ) =>
        ReadAsync<CollectionPage<RelistenCollection>>(async () =>
            await collections.ListAsync(limit, cursor)
        );

    /// <summary>Returns collection identity and distinct playable source, show, and artist counts.</summary>
    [HttpGet("{collectionUuidOrSlug}")]
    [ProducesResponseType(typeof(RelistenCollection), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Get([FromRoute] string collectionUuidOrSlug) =>
        ReadAsync(() => collections.FindAsync(collectionUuidOrSlug));

    /// <summary>Lists artists represented by playable collection recordings, with artist-wide membership labeled.</summary>
    [HttpGet("{collectionUuidOrSlug}/artists")]
    [ProducesResponseType(typeof(CollectionPage<CollectionArtist>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Artists(
        [FromRoute] string collectionUuidOrSlug,
        [FromQuery] int limit = 50,
        [FromQuery] string? cursor = null
    ) =>
        ReadAsync(async () =>
        {
            var collection = await collections.FindAsync(collectionUuidOrSlug);
            return collection == null
                ? null
                : await collections.ArtistsAsync(collection.uuid, limit, cursor);
        });

    /// <summary>
    /// Lists canonical playback references in descending performance-date order. An artist filter
    /// stays inside collection membership; it does not expand to that artist's full catalog.
    /// </summary>
    [HttpGet("{collectionUuidOrSlug}/recordings")]
    [ProducesResponseType(typeof(CollectionPage<CollectionRecording>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Recordings(
        [FromRoute] string collectionUuidOrSlug,
        [FromQuery] int limit = 50,
        [FromQuery] string? cursor = null,
        [FromQuery(Name = "artist_uuid")] Guid? artistUuid = null
    ) =>
        ReadAsync(async () =>
        {
            var collection = await collections.FindAsync(collectionUuidOrSlug);
            return collection == null
                ? null
                : await collections.RecordingsAsync(collection.uuid, limit, cursor, artistUuid);
        });

    private async Task<IActionResult> ReadAsync<T>(Func<Task<T?>> read)
        where T : class
    {
        try
        {
            var result = await read();
            if (result == null)
            {
                return NotFound();
            }

            return new JsonResult(result)
            {
                SerializerSettings = RelistenApiJsonOptionsWrapper.ApiV3SerializerSettings,
            };
        }
        catch (ArgumentException error)
        {
            return BadRequest(
                new ProblemDetails
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "Invalid collection request",
                    Detail = error.Message,
                }
            );
        }
    }
}
