using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Relisten.Api.Models.Api;
using Relisten.Data;

namespace Relisten.Controllers;

/// <summary>Browses accepted global people and venue identities without changing legacy playback responses.</summary>
[ApiController]
[AllowAnonymous]
[Route("api/v3/normalization")]
[Produces("application/json")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[NormalizationReadValidation]
public sealed class NormalizationPeopleVenuesController(NormalizationReadService reads)
    : ControllerBase
{
    [HttpGet("people")]
    public Task<IActionResult> People(
        [FromQuery] string? q = null,
        [FromQuery] int limit = 50,
        [FromQuery] string? cursor = null,
        CancellationToken cancellationToken = default
    ) =>
        ReadAsync(async () =>
            await reads.ListPeopleAsync(
                QueryValue("q", q),
                limit,
                QueryValue("cursor", cursor),
                cancellationToken
            )
        );

    [HttpGet("people/{uuid}")]
    public Task<IActionResult> Person(string uuid, CancellationToken cancellationToken = default) =>
        ReadAsync(() => reads.GetPersonAsync(ParseUuid(uuid), cancellationToken));

    [HttpGet("people/{uuid}/recordings")]
    public Task<IActionResult> PersonRecordings(
        string uuid,
        [FromQuery] string? role = null,
        [FromQuery(Name = "artist_uuid")] string? artistUuid = null,
        [FromQuery] int limit = 50,
        [FromQuery] string? cursor = null,
        CancellationToken cancellationToken = default
    ) =>
        ReadAsync(() =>
            reads.ListPersonRecordingsAsync(
                ParseUuid(uuid),
                QueryValue("role", role),
                ParseOptionalUuid(QueryValue("artist_uuid", artistUuid)),
                limit,
                QueryValue("cursor", cursor),
                cancellationToken
            )
        );

    [HttpGet("venues")]
    public Task<IActionResult> Venues(
        [FromQuery] string? q = null,
        [FromQuery] int limit = 50,
        [FromQuery] string? cursor = null,
        CancellationToken cancellationToken = default
    ) =>
        ReadAsync(async () =>
            await reads.ListVenuesAsync(
                QueryValue("q", q),
                limit,
                QueryValue("cursor", cursor),
                cancellationToken
            )
        );

    [HttpGet("venues/{uuid}")]
    public Task<IActionResult> Venue(string uuid, CancellationToken cancellationToken = default) =>
        ReadAsync(() => reads.GetVenueAsync(ParseUuid(uuid), cancellationToken));

    [HttpGet("venues/{uuid}/shows")]
    public Task<IActionResult> VenueShows(
        string uuid,
        [FromQuery(Name = "artist_uuid")] string? artistUuid = null,
        [FromQuery] int limit = 50,
        [FromQuery] string? cursor = null,
        CancellationToken cancellationToken = default
    ) =>
        ReadAsync(() =>
            reads.ListVenueShowsAsync(
                ParseUuid(uuid),
                ParseOptionalUuid(QueryValue("artist_uuid", artistUuid)),
                limit,
                QueryValue("cursor", cursor),
                cancellationToken
            )
        );

    [HttpGet("recordings/{uuid}/credits")]
    public Task<IActionResult> Credits(
        string uuid,
        [FromQuery] int limit = 50,
        [FromQuery] string? cursor = null,
        CancellationToken cancellationToken = default
    ) =>
        ReadAsync(() =>
            reads.ListRecordingCreditsAsync(
                ParseUuid(uuid),
                limit,
                QueryValue("cursor", cursor),
                cancellationToken
            )
        );

    private async Task<IActionResult> ReadAsync<T>(Func<Task<T?>> read)
        where T : class
    {
        // Set explicitly as well as the MVC cache filter, including direct controller invocations.
        Response.Headers.CacheControl = "no-store";
        try
        {
            var result = await read();
            return result == null
                ? NotFound()
                : new JsonResult(result)
                {
                    SerializerSettings = RelistenApiJsonOptionsWrapper.ApiV3SerializerSettings,
                };
        }
        catch (ArgumentException error)
        {
            return RequestProblem(
                400,
                "invalid_request",
                "Invalid normalization request",
                error.Message
            );
        }
        catch (NormalizationEntityNotFoundException)
        {
            return NotFound();
        }
        catch (NormalizationUnavailableException)
        {
            return RequestProblem(
                503,
                "normalization_unavailable",
                "Normalization reads are unavailable",
                null
            );
        }
    }

    private string? QueryValue(string name, string? boundValue) =>
        Request.Query.ContainsKey(name) ? Request.Query[name].ToString() : boundValue;

    private static Guid ParseUuid(string value) =>
        Guid.TryParse(value, out var uuid)
            ? uuid
            : throw new ArgumentException("A UUID is required.");

    private static Guid? ParseOptionalUuid(string? value) =>
        value == null ? null : ParseUuid(value);

    private ObjectResult RequestProblem(int status, string code, string title, string? detail)
    {
        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = detail,
        };
        problem.Extensions["code"] = code;
        return new ObjectResult(problem)
        {
            StatusCode = status,
            ContentTypes = { "application/problem+json" },
        };
    }
}

/// <summary>Keep model-binding errors on these additive routes consistent with domain validation.</summary>
[AttributeUsage(AttributeTargets.Class)]
internal sealed class NormalizationReadValidationAttribute
    : Attribute,
        IActionFilter,
        IOrderedFilter
{
    public int Order => -3000;

    public void OnActionExecuting(ActionExecutingContext context)
    {
        if (context.ModelState.IsValid)
        {
            return;
        }
        var problem = new ProblemDetails
        {
            Status = 400,
            Title = "Invalid normalization request",
            Detail = "A query parameter has an invalid value.",
        };
        problem.Extensions["code"] = "invalid_request";
        context.HttpContext.Response.Headers.CacheControl = "no-store";
        context.Result = new BadRequestObjectResult(problem);
    }

    public void OnActionExecuted(ActionExecutedContext context) { }
}
