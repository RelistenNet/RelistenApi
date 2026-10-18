using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization.Batch;

/// <summary>
/// Offline admission of the existing native Phish and Archive HTTP capture formats.
/// CatalogManifestPath supplies the original catalog namespace and source coordinates.
/// No provider request, database write or current-input receipt is created.
/// </summary>
public sealed record RetainedHttpProviderInputsRequest(
    string CatalogManifestPath,
    IReadOnlyList<string> HttpCaptureDirectories
);

public sealed record RetainedHttpProviderInputsResult(
    string InputsPath,
    string DescriptorPath,
    int InputRows,
    int BoundSources,
    int UnresolvedSources
);

/// <summary>A byte-authenticated artifact in the retained capture, never a provider currentness claim.</summary>
public sealed record RetainedHttpArtifact(string Path, long Bytes, string Sha256);

/// <summary>
/// An explicit historical recording association supported by the receipt's selected source,
/// provider resource identity and that source's captured artist/identifier. A missing or
/// contradictory catalog coordinate stays unresolved; no date-based source selection occurs.
/// </summary>
public sealed record RetainedHttpCatalogBinding(
    string Status,
    string? Reason,
    Guid ExpectedSourceUuid,
    Guid? SourceUuid,
    Guid? ShowUuid,
    string? ShowReason,
    RetainedHttpArtifact CatalogManifest,
    string? SourceShardPath,
    long? SourceLine
);

/// <summary>
/// Admission is new; HttpStartedAt is the original HTTP observation metadata. InputCapturedAt
/// records this offline admission, not a fabricated original normalization fetch or generation.
/// </summary>
public sealed record RetainedHttpProviderProvenance(
    Guid InputRevisionUuid,
    DateTimeOffset AdmittedAt,
    DateTimeOffset HttpStartedAt,
    string RequestedUrl,
    int HttpStatus,
    RetainedHttpArtifact CaptureManifest,
    RetainedHttpArtifact SelectionPlan,
    RetainedHttpArtifact HttpReceipts,
    int ReceiptOrdinal,
    RetainedHttpArtifact Body,
    RetainedHttpCatalogBinding CatalogBinding
);
