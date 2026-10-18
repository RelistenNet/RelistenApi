using System;
using System.Collections.Generic;

namespace Relisten.Api.Models.Normalization;

/// <summary>
/// A quoted source assertion that one named actor also uses another name. This shape carries
/// no recording role, participation, actor kind or canonical identity assignment.
/// Local references must retain their original input, field and producer scope.
/// </summary>
public sealed record SourceActorAliasAssertion<TEvidence>(
    string Version,
    Guid AssertionUuid,
    Guid ActorReferenceUuid,
    string FieldSha256,
    string Name,
    TEvidence NameEvidence,
    IReadOnlyList<ActorAliasFact<TEvidence>> Aliases,
    IReadOnlyList<ActorCoReferenceFact<TEvidence>> Relationships,
    SourceActorAliasContext<TEvidence> Context,
    IReadOnlyList<ActorEvidenceUnresolved<TEvidence>> Unresolved
);

/// <summary>These are interpretation citations, not assertions that an actor performed.</summary>
public sealed record SourceActorAliasContext<TEvidence>(
    TEvidence Heading,
    IReadOnlyList<TEvidence> Qualifiers,
    TEvidence Statement
);

/// <summary>
/// Complete concerns this producer's original field, not exhaustive knowledge of a person's
/// aliases. Missing fields, unclassified alias markers and parse bounds prevent retirement.
/// </summary>
public sealed record SourceActorAliasScopeCoverage<TEvidence>(
    string Field,
    bool Complete,
    string Reason,
    IReadOnlyList<TEvidence> Evidence
);

/// <summary>Quoted assertions, unresolved statements and coverage of each original field.</summary>
public sealed record SourceActorAliasExtraction<TEvidence>(
    IReadOnlyList<SourceActorAliasAssertion<TEvidence>> Assertions,
    IReadOnlyList<ActorEvidenceUnresolved<TEvidence>> Unresolved,
    IReadOnlyList<SourceActorAliasScopeCoverage<TEvidence>> Scopes
);
