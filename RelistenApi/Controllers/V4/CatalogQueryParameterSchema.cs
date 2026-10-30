using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Relisten.Controllers.V4;

/// <summary>Publishes the same closed query choices that MVC validates with AllowedValues.</summary>
internal sealed class CatalogQueryParameterSchema : IParameterFilter
{
    public void Apply(IOpenApiParameter parameter, ParameterFilterContext context)
    {
        if (
            parameter.Schema is OpenApiSchema schema
            && context.ParameterInfo?.GetCustomAttribute<AllowedValuesAttribute>() is { } allowed
        )
        {
            schema.Enum = allowed
                .Values.Select(value => (JsonNode)JsonValue.Create(value.ToString())!)
                .ToList();
        }
    }
}
