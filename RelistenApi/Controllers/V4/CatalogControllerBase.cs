using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Npgsql;
using Relisten.Catalog;
using Relisten.Identifiers;

namespace Relisten.Controllers.V4;

/// <summary>Owns v4 read validation, dependency failures and response publication.</summary>
public abstract class CatalogControllerBase(ILogger logger) : ControllerBase
{
    protected Task<IActionResult> Read<T>(
        string id,
        string prefix,
        Func<Guid, Task<T?>> read,
        bool cacheable = true
    )
        where T : class =>
        UuidTypeId.TryParse(id, prefix, out var uuid)
            ? Execute(() => read(uuid), cacheable)
            : Task.FromResult<IActionResult>(InvalidId(prefix));

    protected async Task<IActionResult> Execute<T>(Func<Task<T?>> read, bool cacheable = true)
        where T : class
    {
        if (!ModelState.IsValid)
        {
            return Failure(422, "invalid_query", "The query contains an invalid value.");
        }

        try
        {
            var resource = await read();
            return resource is null
                ? Failure(404, "not_found", "The requested resource does not exist.")
                : new CatalogJsonResult(resource, cacheable);
        }
        catch (CatalogRetiredResourceException)
        {
            return Failure(410, "retired", "The requested catalog identity has been retired.");
        }
        catch (CatalogNormalizationUnavailableException exception)
        {
            logger.LogWarning(
                exception,
                "Canonical catalog read failed for {RequestPath}",
                Request.Path
            );
            return Failure(
                503,
                "normalization_unavailable",
                "The normalized catalog is temporarily unavailable."
            );
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException)
        {
            logger.LogError(exception, "Catalog read failed for {RequestPath}", Request.Path);
            return Failure(503, "catalog_unavailable", "The catalog is temporarily unavailable.");
        }
    }

    protected JsonResult InvalidId(string prefix) =>
        Failure(422, "invalid_id", "Expected a canonical " + prefix + " ID.");

    protected JsonResult Failure(int status, string code, string title)
    {
        Response.Headers.CacheControl = "no-store";
        var problem = new ProblemDetails { Status = status, Title = title };
        problem.Extensions["code"] = code;
        // JsonResult keeps controller-level Produces(application/json) from overriding errors.
        return new JsonResult(problem)
        {
            StatusCode = status,
            ContentType = "application/problem+json",
        };
    }
}
