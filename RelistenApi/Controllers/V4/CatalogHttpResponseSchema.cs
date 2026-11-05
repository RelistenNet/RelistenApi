using System.Collections.Generic;
using System.Linq;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Relisten.Controllers.V4;

/// <summary>Documents the HTTP publication rules shared by native catalog controllers.</summary>
internal sealed class CatalogHttpResponseSchema : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (context.ApiDescription.GroupName != "v4")
        {
            return;
        }

        foreach (var (status, value) in operation.Responses!)
        {
            if (
                int.TryParse(status, out var code)
                && code >= 400
                && value is OpenApiResponse response
            )
            {
                // Failure() publishes ProblemDetails explicitly, even when the
                // controller's successful responses use Produces(application/json).
                response.Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["application/problem+json"] = response.Content!.Values.Single(),
                };
            }
        }

        // Random and live history are the two no-store GETs. POSTs never revalidate.
        if (
            context.ApiDescription.HttpMethod != "GET"
            || context.MethodInfo.DeclaringType == typeof(CatalogLiveController)
                && context.MethodInfo.Name == nameof(CatalogLiveController.History)
            || context.MethodInfo.DeclaringType == typeof(CatalogShowDiscoveryController)
                && context.MethodInfo.Name == nameof(CatalogShowDiscoveryController.Random)
        )
        {
            return;
        }

        operation.Parameters ??= [];
        operation.Parameters.Add(
            new OpenApiParameter
            {
                Name = "If-None-Match",
                In = ParameterLocation.Header,
                Description = "Revalidate a previously applied response using its ETag.",
                Schema = new OpenApiSchema { Type = JsonSchemaType.String },
            }
        );
        operation.Responses["304"] = new OpenApiResponse
        {
            Description = "Not Modified; retain the previously applied response. No body.",
        };
        foreach (var status in new[] { "200", "304" })
        {
            var response = (OpenApiResponse)operation.Responses[status];
            response.Headers ??= new Dictionary<string, IOpenApiHeader>();
            response.Headers["ETag"] = new OpenApiHeader
            {
                Description = "Weak validator for the represented JSON; not an ordered revision.",
                Schema = new OpenApiSchema { Type = JsonSchemaType.String },
            };
        }
    }
}
