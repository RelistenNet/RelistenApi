using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Relisten.Api.Models.Normalization;

/// <summary>A reviewed reference. Frozen-source keys never acquire canonical UUID meaning.</summary>
public sealed record ExpertKnowledgeTarget(
    string Domain,
    string Namespace,
    string StableKey,
    Guid? CanonicalUuid = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        Guid? ImportedSongUuid = null
);

/// <summary>
/// An exact account selector, or an exact literal alias constrained by context. Every populated
/// field is required to match. Dates are inclusive; missing observation context cannot match it.
/// </summary>
public sealed record ExpertKnowledgeSelector(
    string Namespace,
    string? AccountStableKey = null,
    string? Literal = null,
    Guid? ArtistUuid = null,
    string? Provider = null,
    string? Locality = null,
    DateOnly? From = null,
    DateOnly? Through = null
);

/// <summary>The operator host retains this small immutable evidence file before applying review.</summary>
public sealed record ExpertKnowledgeEvidencePin(string Path, long Bytes, string Sha256);

public sealed record ExpertKnowledgeDefinition(
    string Domain,
    ExpertKnowledgeSelector Selector,
    ExpertKnowledgeTarget Target,
    IReadOnlyList<ExpertKnowledgeEvidencePin> Evidence,
    string EvidenceDescription
);

/// <summary>
/// ExpectedRevision zero creates a rule. Accept and reject carry its complete replacement
/// definition; retract keeps the last definition as history. Operator identity is never a field.
/// </summary>
public sealed record ExpertKnowledgeSaveCommand(
    Guid OperationUuid,
    Guid RuleUuid,
    long ExpectedRevision,
    string Action,
    ExpertKnowledgeDefinition? Definition,
    string Reason
);

public sealed record ExpertKnowledgeRule(
    Guid RuleUuid,
    long Revision,
    string State,
    ExpertKnowledgeDefinition Definition,
    Guid LastOperationUuid,
    DateTime UpdatedAt
);

public sealed record ExpertKnowledgeSaveResult(
    Guid RuleUuid,
    long Revision,
    string State,
    bool Replayed
);

public sealed record ExpertKnowledgeHistoryEntry(
    Guid OperationUuid,
    long Revision,
    string Action,
    string Reason,
    string Operator,
    DateTime CreatedAt,
    ExpertKnowledgeSaveCommand Command
);

/// <summary>One immutable policy image shared by batch matching and the operator reader bridge.</summary>
public sealed record ExpertKnowledgeSnapshot(
    string Version,
    string Sha256,
    IReadOnlyList<ExpertKnowledgeRule> Rules
);

public sealed record ExpertKnowledgeObservation(
    string Domain,
    string Namespace,
    string AccountStableKey,
    string? Literal = null,
    Guid? ArtistUuid = null,
    string? Provider = null,
    string? Locality = null,
    DateOnly? Date = null
);

public sealed record ExpertKnowledgeAppliedRule(Guid RuleUuid, long Revision, string State);

/// <summary>
/// Positive correspondence and named-target exclusions are separate. No result creates an
/// identity, asserts identical audio, merges accounts or converts an observation into a credit.
/// </summary>
public sealed record ExpertKnowledgeEvaluation(
    string Outcome,
    string SnapshotSha256,
    ExpertKnowledgeTarget? Target,
    IReadOnlyList<ExpertKnowledgeTarget> ExcludedTargets,
    IReadOnlyList<ExpertKnowledgeAppliedRule> AppliedRules,
    IReadOnlyList<ExpertKnowledgeTarget> ConflictingTargets
);
