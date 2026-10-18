using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Relisten.Api.Models.Normalization;

/// <summary>
/// Citation into an original named string field. Offsets are zero-based Unicode code points,
/// with an exclusive end. Quote is the exact raw substring, including any HTML or entities.
/// </summary>
public sealed record NormalizationTextEvidence(
    string Field,
    string JsonPointer,
    string Quote,
    int StartCodePoint,
    int EndCodePoint
);

public enum RecordingCreditRole
{
    Taper,
    Transferrer,
    MatrixBuilder,
    MasteringEngineer,
    RecordingEngineer,
    Uploader,
    Collector,
    Musician,
    Other,
}

/// <summary>A credited wording and role, without an inferred global person identity.</summary>
public sealed record CreditMention(
    string Name,
    RecordingCreditRole Role,
    NormalizationTextEvidence Evidence,
    string? OriginalRole = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        CreditedActorFacts<NormalizationTextEvidence>? ActorFacts = null
);

/// <summary>An observed uncertainty requiring additional extraction or review.</summary>
public sealed record RecordingExtractionUnresolved(
    string Reason,
    NormalizationTextEvidence Evidence
);

public sealed record RecordingCreditExtraction(
    IReadOnlyList<CreditMention> Credits,
    IReadOnlyList<RecordingExtractionUnresolved> Unresolved
);
