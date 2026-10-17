using System.Collections.Generic;

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

/// <summary>
/// An admitted catalog refresh and the artist revisions read with its exact facts. The capture
/// can become historical after this method returns. Checked binding and work admission must
/// retain these revisions; rebinding must not replace them with newer catalog revisions.
/// Unchanged bytes reuse an input revision, while the capture receipt advances its resource generation.
/// </summary>
public sealed record NormalizationLegacyRecordingSnapshot(
    NormalizationCaptureResult Capture,
    IReadOnlyList<NormalizationCatalogDependency> CatalogInputs
);
