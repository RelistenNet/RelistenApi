using System.Collections.Generic;

namespace Relisten.Api.Models.Normalization;

/// <summary>A role-bearing mention with original-input evidence, without an accepted person identity.</summary>
public sealed record RetainedCreditMention(
    string Name,
    RecordingCreditRole Role,
    NormalizedEvidenceReference Evidence,
    string? OriginalRole = null
);

public sealed record RetainedCreditUnresolved(string Reason, NormalizedEvidenceReference Evidence);

public sealed record RetainedCreditExtraction(
    IReadOnlyList<RetainedCreditMention> Credits,
    IReadOnlyList<RetainedCreditUnresolved> Unresolved
);
