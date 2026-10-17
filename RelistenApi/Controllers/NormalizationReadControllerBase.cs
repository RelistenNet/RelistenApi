using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Relisten.Api.Models.Api;
using Relisten.Data;

namespace Relisten.Controllers;

/// <summary>Shared transport contract for additive, editable normalization reads.</summary>
public abstract class NormalizationReadControllerBase : ControllerBase
{
    protected async Task<IActionResult> ReadAsync<T>(Func<Task<T?>> read)
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

    protected string? QueryValue(string name, string? boundValue) =>
        Request.Query.ContainsKey(name) ? Request.Query[name].ToString() : boundValue;

    protected static Guid ParseUuid(string value) =>
        Guid.TryParse(value, out var uuid)
            ? uuid
            : throw new ArgumentException("A UUID is required.");

    protected static Guid? ParseOptionalUuid(string? value) =>
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
