using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Relisten.Api.Models.Api.V4;
using Relisten.Catalog;
using Relisten.Identifiers;

namespace Relisten.Controllers.V4;

/// <summary>Restores saved catalog references without replacing library or catalog membership.</summary>
[Route("api/v4/catalog")]
[ApiExplorerSettings(GroupName = "v4")]
[Produces("application/json")]
public sealed class CatalogResolveController(
    CatalogResolver resolver,
    ILogger<CatalogResolveController> logger
) : CatalogControllerBase(logger)
{
    [HttpPost("resolve")]
    [Consumes("application/json")]
    [RequestSizeLimit(CatalogResolveValidation.MaxBodyBytes)]
    [CatalogResolveBodyLimit]
    [ProducesResponseType(typeof(CatalogResolveResource), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 400)]
    [ProducesResponseType(typeof(ProblemDetails), 413)]
    [ProducesResponseType(typeof(ProblemDetails), 422)]
    [ProducesResponseType(typeof(ProblemDetails), 503)]
    public Task<IActionResult> Resolve(
        [FromBody] CatalogResolveRequest? request,
        CancellationToken cancellationToken
    )
    {
        if (!ModelState.IsValid)
        {
            return Task.FromResult<IActionResult>(
                Failure(400, "invalid_json", "The request must contain valid JSON.")
            );
        }
        if (!CatalogResolveValidation.TryRead(request, out var references, out var error))
        {
            return Task.FromResult<IActionResult>(Failure(422, "invalid_reference", error!));
        }

        return Execute(
            async () =>
            {
                var result = await resolver.Resolve(references, cancellationToken);
                return new CatalogResolveResource(
                    result
                        .References.Select(item => new CatalogReferenceResolutionResource(
                            item.Reference.Kind,
                            item.Reference.Scheme,
                            item.Reference.Id,
                            item.Reference.ArtistUuid is { } artist ? new ArtistId(artist) : null,
                            item.Availability,
                            item.Target is { } target ? new(target.Kind, target.Id) : null,
                            item.Reason
                        ))
                        .ToArray(),
                    V4CatalogProjection.ResolvedEntities(result.Entities)
                );
            },
            cacheable: false
        );
    }
}
