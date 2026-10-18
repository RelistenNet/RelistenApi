using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization.Batch;

/// <summary>Every decoded file in the original provider input, without a file-count cutoff.</summary>
public sealed record ShadowNativeFileContentsProposal(
    IReadOnlyList<ShadowNativeFileContents> Files,
    IReadOnlyList<ProviderDecodeIssue> DecoderIssues,
    IReadOnlyList<ShadowProviderObservationIssue> InputIssues
);

/// <summary>
/// FileOrdinal addresses this proposal's Files array; it does not assert musical order.
/// File retains native observations. Catalog UUIDs and playback are optional exact historical
/// associations; a native filename or locator never becomes a catalog playback URL.
/// </summary>
public sealed record ShadowNativeFileContents(
    int FileOrdinal,
    string EntityKey,
    Guid InputRevisionUuid,
    ProviderObservationOrigin RecordingOrigin,
    ProviderFileObservation File,
    Guid? CatalogSourceUuid,
    Guid? CatalogShowUuid,
    Guid? CatalogTrackUuid,
    Guid? CatalogSourceSetUuid,
    string? CatalogBindingReason,
    ShadowFilePlayback? CatalogPlayback,
    TrackTitleExtraction? TitleInterpretation,
    IReadOnlyList<ShadowNativeFileTitleMention> Mentions,
    IReadOnlyList<ProviderDecodeIssue> Issues,
    string Outcome,
    string? Reason,
    IReadOnlyList<string> EntityKeys
);

/// <summary>
/// Interpretation preserves the shared title/repertoire policy. Evidence remaps its title
/// offsets to the original provider field; neither representation supplies audio boundaries.
/// </summary>
public sealed record ShadowNativeFileTitleMention(
    ShadowRepertoireSegment Interpretation,
    NormalizedEvidenceReference Evidence
);
