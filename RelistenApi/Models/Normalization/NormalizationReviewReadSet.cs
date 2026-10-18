using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Relisten.Api.Models.Normalization;

/// <summary>
/// Original facts actually consumed by a trusted one-off review. The domain authenticates its
/// completeness; Work compares the supplied proof atomically. Observed/dependency owners are
/// read-only unless also named as actual command owners. No Work graph or configuration is made.
/// An absent family means it was not read, not an empty result in an unobserved global pool.
/// </summary>
public sealed record NormalizationReviewReadSet(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<NormalizationInputDependency>? Inputs = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<NormalizationSubjectDependency>? Dependencies = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<NormalizationCatalogDependency>? CatalogInputs = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<NormalizationSourceIdentityDependency>? SourceIdentities = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        NormalizationMappingRegistryDependency? MappingRegistry = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<NormalizationImportedVenueDependency>? ImportedVenues = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<NormalizationCandidateUniverseDependency>? CandidateUniverses = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<NormalizationSubjectObservation>? OwnerObservations = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<NormalizationSourceUuidDependency>? SourceUuids = null
);
