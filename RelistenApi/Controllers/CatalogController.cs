using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Relisten.Api.Models.Api;
using Relisten.Data;

namespace Relisten.Controllers
{
    [ApiController]
    [AllowAnonymous]
    [Route("api/v3/catalog")]
    [Produces("application/json")]
    public sealed class CatalogController : ControllerBase
    {
        private readonly CatalogReferenceResolver _resolver;

        public CatalogController(CatalogReferenceResolver resolver)
        {
            _resolver = resolver;
        }

        [HttpPost("resolve")]
        [RequestSizeLimit(CatalogResolveRequestValidator.MaxRequestBodySizeBytes)]
        [ProducesResponseType(typeof(CatalogResolveResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
        [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
        public async Task<IActionResult> Resolve([FromBody] CatalogResolveRequest request)
        {
            if (!CatalogResolveRequestValidator.TryValidate(request, out var references, out var error))
            {
                return InvalidRequest(error!);
            }

            return new JsonResult(await _resolver.Resolve(references))
            {
                SerializerSettings = RelistenApiJsonOptionsWrapper.ApiV3SerializerSettings
            };
        }

        private UnprocessableEntityObjectResult InvalidRequest(CatalogResolveValidationError error)
        {
            var problem = new ProblemDetails
            {
                Type = $"https://relisten.net/problems/{error.Code}",
                Title = "Invalid catalog resolver request",
                Detail = error.Detail,
                Status = StatusCodes.Status422UnprocessableEntity,
                Instance = HttpContext.Request.Path
            };
            problem.Extensions["code"] = error.Code;

            return UnprocessableEntity(problem);
        }
    }
}
