using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.OpenApi;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Relisten.Api.Models.Api.V4;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Relisten.Controllers.V4;

/// <summary>Documents always-present response fields and nullable v4 values accurately.</summary>
internal sealed class CatalogResourceSchemaFilter : ISchemaFilter
{
    private static readonly DefaultContractResolver Resolver = new();

    public void Apply(IOpenApiSchema schema, SchemaFilterContext context)
    {
        if (
            schema is not OpenApiSchema model
            || schema.Properties is null
            || context.Type.Namespace != typeof(TrackResource).Namespace
            || Resolver.ResolveContract(context.Type) is not JsonObjectContract contract
        )
        {
            return;
        }

        // Responses include nullable fields unless explicitly omitted by their JSON
        // contract. Required means present, not non-null. Requests keep their own rules.
        if (context.Type.Name.EndsWith("Resource", StringComparison.Ordinal))
        {
            model.Required = schema
                .Properties.Keys.Except(
                    contract
                        .Properties.Where(property =>
                            property.NullValueHandling == NullValueHandling.Ignore
                        )
                        .Select(property => property.PropertyName!)
                )
                .ToHashSet();
        }

        if (
            typeof(ArtistResource).IsAssignableFrom(context.Type)
            && schema.Properties.TryGetValue("features", out var featureSchema)
            && featureSchema is OpenApiSchema features
        )
        {
            var flags = Enum.GetValues<ArtistFeatures>()
                .Where(flag => flag != ArtistFeatures.None)
                .ToArray();
            var flagValues = new JsonObject();
            foreach (var flag in flags)
            {
                flagValues.Add(flag.ToString(), flag.ToString("D"));
            }
            features.AddExtension("x-feature-flags", new JsonNodeExtension(flagValues));

            features.Type = JsonSchemaType.String;
            features.Pattern = "^(0|[1-9][0-9]*)$";
            features.MaxLength = 20;
            features.Description =
                "Unsigned 64-bit capability mask as a decimal string. Decode with BigInt in JavaScript; unset bits are false. Ignore unknown bits. Fixed values: "
                + string.Join(", ", flags.Select(flag => $"{flag}={flag:D}"));
        }

        var nullability = new NullabilityInfoContext();
        foreach (var property in contract.Properties)
        {
            if (property.NullValueHandling == NullValueHandling.Ignore)
            {
                // These fields are absent when null, so a present value is non-null.
                if (schema.Properties[property.PropertyName!] is OpenApiSchema optional)
                {
                    optional.Type &= ~JsonSchemaType.Null;
                }
                continue;
            }

            var member = property.DeclaringType?.GetProperty(property.UnderlyingName!);
            if (Nullable.GetUnderlyingType(property.PropertyType!) is { IsEnum: true } enumType)
            {
                var generated = context.SchemaGenerator.GenerateSchema(
                    enumType,
                    context.SchemaRepository
                );
                // Nullable request properties let MVC detect missing values, but
                // Required rejects null at the HTTP boundary.
                if (member?.IsDefined(typeof(RequiredAttribute)) == true)
                {
                    schema.Properties[property.PropertyName!] = generated;
                    continue;
                }
                var values = generated is OpenApiSchemaReference enumReference
                    ? context.SchemaRepository.Schemas[enumReference.Reference.Id!].Enum
                    : generated.Enum;
                schema.Properties[property.PropertyName!] = new OpenApiSchema
                {
                    Type = JsonSchemaType.String | JsonSchemaType.Null,
                    Enum = values!.Append(null!).ToList(),
                };
                continue;
            }

            if (
                member is null
                || nullability.Create(member).ReadState != NullabilityState.Nullable
                || schema.Properties[property.PropertyName!] is not OpenApiSchemaReference reference
            )
            {
                continue;
            }

            // A nullable sibling of $ref is ignored in OpenAPI 3.0. Copy only this
            // property schema; shared definitions and required consumers stay non-nullable.
            var target = context.SchemaRepository.Schemas[reference.Reference.Id!];
            if (target.Type == JsonSchemaType.Object)
            {
                var nullable = (OpenApiSchema)target.CreateShallowCopy();
                nullable.Type = JsonSchemaType.Object | JsonSchemaType.Null;
                schema.Properties[property.PropertyName!] = nullable;
            }
        }
    }
}
