using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.OpenApi;
using Relisten.Api.Models.Api.V4;
using Relisten.Catalog;
using Relisten.Identifiers;
using Relisten.Normalization;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Relisten.Controllers.V4;

/// <summary>Validates the bounded resolver protocol before any catalog queries run.</summary>
internal static class CatalogResolveValidation
{
    internal const int MaxReferences = 500;
    internal const long MaxBodyBytes = 128 * 1024;

    public static bool TryRead(
        CatalogResolveRequest? request,
        out IReadOnlyList<CatalogReference> references,
        out string? error
    )
    {
        references = [];
        if (request?.References is not { Count: > 0 and <= MaxReferences } input)
        {
            error = "references must contain between 1 and 500 items.";
            return false;
        }

        var parsed = new List<CatalogReference>(input.Count);
        for (var index = 0; index < input.Count; index++)
        {
            var item = input[index];
            if (
                item is null
                || !NormalizationEnums.TryParse<CatalogReferenceKind>(item.Kind, out var kind)
                || !NormalizationEnums.TryParse<CatalogReferenceScheme>(item.Scheme, out var scheme)
                || !CatalogReferenceLookup.Supports(kind, scheme)
                || !ValidIdentity(kind, scheme, item.Id)
            )
            {
                error = $"references[{index}] must specify a supported kind, scheme, and valid ID.";
                return false;
            }

            Guid? artist = null;
            if (item.ArtistId is not null)
            {
                if (!ArtistId.TryParse(item.ArtistId, out var id))
                {
                    error = $"references[{index}].artist_id must be an artist_ ID.";
                    return false;
                }
                artist = id.Value;
            }

            if (
                scheme is CatalogReferenceScheme.LegacyInteger or CatalogReferenceScheme.LegacySlug
                && kind is not (CatalogReferenceKind.Artist or CatalogReferenceKind.Venue)
                && artist is null
            )
            {
                error = $"references[{index}] requires artist_id for this legacy identity.";
                return false;
            }

            parsed.Add(new(kind, scheme, item.Id!, artist));
        }

        references = parsed;
        error = null;
        return true;
    }

    private static bool ValidIdentity(
        CatalogReferenceKind kind,
        CatalogReferenceScheme scheme,
        string? id
    )
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        switch (scheme)
        {
            case CatalogReferenceScheme.LegacyUuid:
                return Guid.TryParseExact(id, "D", out _);
            case CatalogReferenceScheme.LegacyInteger:
                return long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
                    && n > 0;
            case CatalogReferenceScheme.LegacySlug:
                return true;
            default:
                if (
                    kind
                    is not (
                        CatalogReferenceKind.Song
                        or CatalogReferenceKind.Venue
                        or CatalogReferenceKind.ArtistVenue
                    )
                )
                {
                    return UuidTypeId.TryParse(id, CatalogReferenceLookup.Prefix(kind), out _);
                }

                try
                {
                    switch (kind)
                    {
                        case CatalogReferenceKind.Song:
                            SongId.Parse(id);
                            break;
                        case CatalogReferenceKind.Venue:
                            VenueId.Parse(id);
                            break;
                        case CatalogReferenceKind.ArtistVenue:
                            ArtistVenueId.Parse(id);
                            break;
                    }
                    return true;
                }
                catch (FormatException)
                {
                    return false;
                }
        }
    }
}

/// <summary>Publishes closed vocabularies while retaining semantic validation of raw JSON tokens.</summary>
internal sealed class CatalogResolveRequestSchema : ISchemaFilter, IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (
            context.MethodInfo.DeclaringType == typeof(CatalogResolveController)
            && operation.RequestBody is OpenApiRequestBody body
        )
        {
            body.Required = true;
        }
    }

    public void Apply(IOpenApiSchema schema, SchemaFilterContext context)
    {
        if (schema is not OpenApiSchema model || model.Properties is null)
        {
            return;
        }

        if (context.Type == typeof(CatalogResolveRequest))
        {
            model.Required = new HashSet<string> { "references" };
            var references = (OpenApiSchema)model.Properties["references"];
            references.Type = JsonSchemaType.Array;
            references.MinItems = 1;
            references.MaxItems = CatalogResolveValidation.MaxReferences;
            return;
        }
        if (context.Type != typeof(CatalogReferenceRequest))
        {
            return;
        }

        model.Required = new HashSet<string> { "kind", "scheme", "id" };
        model.Properties["kind"] = context.SchemaGenerator.GenerateSchema(
            typeof(CatalogReferenceKind),
            context.SchemaRepository
        );
        model.Properties["scheme"] = context.SchemaGenerator.GenerateSchema(
            typeof(CatalogReferenceScheme),
            context.SchemaRepository
        );
        model.Properties["id"] = new OpenApiSchema { Type = JsonSchemaType.String };
        model.Properties["artist_id"] = new OpenApiSchema
        {
            Type = JsonSchemaType.String | JsonSchemaType.Null,
            Pattern = context
                .SchemaGenerator.GenerateSchema(typeof(ArtistId), context.SchemaRepository)
                .Pattern,
        };
    }
}

/// <summary>Retains the resolver's error contract when Kestrel rejects a streaming JSON body.</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class CatalogResolveBodyLimitAttribute : ExceptionFilterAttribute
{
    public override void OnException(ExceptionContext context)
    {
        if (context.Exception is not BadHttpRequestException { StatusCode: 413 })
        {
            return;
        }

        var problem = new ProblemDetails
        {
            Status = 413,
            Title = "Resolver requests may contain at most 128 KiB.",
        };
        problem.Extensions["code"] = "request_too_large";
        context.HttpContext.Response.Headers.CacheControl = "no-store";
        context.Result = new ObjectResult(problem)
        {
            StatusCode = 413,
            ContentTypes = { "application/problem+json" },
        };
        context.ExceptionHandled = true;
    }
}
