using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Relisten.Api.Models.Normalization;

/// <summary>A role-bearing mention with original-input evidence, without an accepted person identity.</summary>
public sealed record RetainedCreditMention(
    string Name,
    RecordingCreditRole Role,
    NormalizedEvidenceReference Evidence,
    string? OriginalRole = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        CreditedActorFacts<NormalizedEvidenceReference>? ActorFacts = null
);

/// <summary>A file-scoped issue retains its provider origin even when the file has no catalog binding.</summary>
public sealed record RetainedCreditUnresolved(
    string Reason,
    NormalizedEvidenceReference Evidence,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        ProviderObservationOrigin? FileOrigin = null
);

public sealed record RetainedCreditExtraction(
    IReadOnlyList<RetainedCreditMention> Credits,
    IReadOnlyList<RetainedCreditUnresolved> Unresolved
);
