namespace Relisten.Api.Models.Normalization;

/// <summary>Whether a recording snapshot was retained, already existed, or had no catalog source.</summary>
public enum NormalizationLegacySeedDisposition
{
    Seeded,
    Existing,
    Missing,
}

/// <summary>
/// A zero-generation catalog seed. Input is absent only for Missing; Existing includes a concurrent
/// seed winner. A catalog snapshot cannot recover metadata discarded by earlier provider imports.
/// </summary>
public sealed record NormalizationLegacySeedResult(
    NormalizationLegacySeedDisposition Disposition,
    NormalizationCurrentInput? Input
);
