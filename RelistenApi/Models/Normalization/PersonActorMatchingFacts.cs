using System.Collections.Generic;

namespace Relisten.Api.Models.Normalization;

/// <summary>
/// Complete, deduplicated matching values from accepted actor accounts. Raw supporting
/// accounts remain separate bounded witnesses. SourceKinds never replaces canonical person kind.
/// Completeness is reported by the enclosing candidate snapshot, including distinct-value bounds.
/// </summary>
public sealed record PersonActorMatchingFacts(
    bool HasActorContext,
    IReadOnlyList<string> CreditedNames,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<PeopleContactLookup> Contacts,
    IReadOnlyList<PeopleProfileLookup> Profiles,
    IReadOnlyList<string> SourceKinds,
    bool HasAttachmentUncertainty,
    bool HasConflictingKind
);
