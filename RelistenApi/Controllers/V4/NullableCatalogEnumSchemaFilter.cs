using System;
using System.Linq;
using Microsoft.OpenApi;
using Newtonsoft.Json.Serialization;
using Relisten.Api.Models.Api.V4;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Relisten.Controllers.V4;

/// <summary>Preserves nullable enum values in v4's OpenAPI 3.0 property schemas.</summary>
internal sealed class NullableCatalogEnumSchemaFilter : ISchemaFilter
{
    private static readonly DefaultContractResolver Resolver = new();

    public void Apply(IOpenApiSchema schema, SchemaFilterContext context)
    {
        if (
            schema.Properties is null
            || context.Type.Namespace != typeof(TrackResource).Namespace
            || Resolver.ResolveContract(context.Type) is not JsonObjectContract contract
        )
        {
            return;
        }

        foreach (var property in contract.Properties)
        {
            if (Nullable.GetUnderlyingType(property.PropertyType!) is not { IsEnum: true } enumType)
            {
                continue;
            }

            // The generator loses Nullable<T> on a bare enum reference. Inline a
            // nullable copy at this property; the shared enum and legacy schemas stay intact.
            var generated = context.SchemaGenerator.GenerateSchema(
                enumType,
                context.SchemaRepository
            );
            var values = generated is OpenApiSchemaReference reference
                ? context.SchemaRepository.Schemas[reference.Reference.Id!].Enum
                : generated.Enum;
            schema.Properties![property.PropertyName!] = new OpenApiSchema
            {
                Type = JsonSchemaType.String | JsonSchemaType.Null,
                Enum = values!.Append(null).ToList(),
            };
        }
    }
}
