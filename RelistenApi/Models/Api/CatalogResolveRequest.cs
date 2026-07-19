using System;
using System.Collections.Generic;

namespace Relisten.Api.Models.Api
{
    public sealed class CatalogResolveRequest
    {
        public int contract_version { get; set; }
        public IReadOnlyList<CatalogReferenceRequest>? references { get; set; }
    }

    public sealed class CatalogReferenceRequest
    {
        public string? catalog_type { get; set; }
        public string? catalog_uuid { get; set; }
    }

    public sealed record CatalogReference(string CatalogType, Guid CatalogUuid);

    public sealed record CatalogResolveValidationError(string Code, string Detail);

    public static class CatalogResolveRequestValidator
    {
        public const int ContractVersion = 1;
        public const int MaxReferenceCount = 500;

        private static readonly HashSet<string> SupportedCatalogTypes = new(StringComparer.Ordinal)
        {
            "artist",
            "show",
            "source",
            "source_track",
            "song",
            "tour",
            "venue"
        };

        public static bool TryValidate(
            CatalogResolveRequest? request,
            out IReadOnlyList<CatalogReference> references,
            out CatalogResolveValidationError? error)
        {
            references = Array.Empty<CatalogReference>();

            if (request == null)
            {
                error = new CatalogResolveValidationError(
                    "request_required",
                    "A JSON request body is required.");
                return false;
            }

            if (request.contract_version != ContractVersion)
            {
                error = new CatalogResolveValidationError(
                    "unsupported_contract_version",
                    $"contract_version must be {ContractVersion}.");
                return false;
            }

            if (request.references == null)
            {
                error = new CatalogResolveValidationError(
                    "references_required",
                    "references must be a JSON array.");
                return false;
            }

            if (request.references.Count == 0)
            {
                error = new CatalogResolveValidationError(
                    "references_required",
                    "references must contain at least one catalog reference.");
                return false;
            }

            var uniqueReferences = new List<CatalogReference>();
            var seen = new HashSet<CatalogReference>();

            for (var index = 0; index < request.references.Count; index++)
            {
                var candidate = request.references[index];
                if (candidate == null)
                {
                    error = InvalidReference(index, "must be an object");
                    return false;
                }

                if (candidate.catalog_type == null || !SupportedCatalogTypes.Contains(candidate.catalog_type))
                {
                    error = InvalidReference(index,
                        "catalog_type must be one of artist, show, source, source_track, song, tour, or venue");
                    return false;
                }

                if (candidate.catalog_uuid == null ||
                    !Guid.TryParseExact(candidate.catalog_uuid, "D", out var catalogUuid) ||
                    catalogUuid == Guid.Empty)
                {
                    error = InvalidReference(index,
                        "catalog_uuid must be a non-empty UUID in canonical hyphenated form");
                    return false;
                }

                var catalogReference = new CatalogReference(candidate.catalog_type, catalogUuid);
                if (!seen.Add(catalogReference))
                {
                    continue;
                }

                uniqueReferences.Add(catalogReference);
                if (uniqueReferences.Count > MaxReferenceCount)
                {
                    error = new CatalogResolveValidationError(
                        "too_many_references",
                        $"references may contain at most {MaxReferenceCount} distinct catalog references.");
                    return false;
                }
            }

            references = uniqueReferences;
            error = null;
            return true;
        }

        private static CatalogResolveValidationError InvalidReference(int index, string reason)
        {
            return new CatalogResolveValidationError(
                "invalid_catalog_reference",
                $"references[{index}] {reason}.");
        }
    }
}
