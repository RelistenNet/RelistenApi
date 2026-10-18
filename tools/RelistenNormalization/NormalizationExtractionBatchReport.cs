using System.Text.Json;
using System.Text.Json.Serialization;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization.Batch;

/// <summary>Exact inputs and provisional outputs for one retained catalog resource.</summary>
public sealed record NormalizationBatchExtraction(
    string Kind,
    long ResourceId,
    Guid ResourceUuid,
    long ArtistId,
    Guid ArtistUuid,
    string? Family,
    IReadOnlyDictionary<string, string?> Inputs,
    NormalizationTrackInputContext? TrackContext,
    RecordingCreditExtraction? CreditExtraction,
    TrackTitleExtraction? TitleExtraction,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        SourceActorAliasExtraction<NormalizationTextEvidence>? SourceActorAliases = null
);

/// <summary>Retained context availability; null does not establish playback availability.</summary>
public sealed record NormalizationTrackInputContext(
    Guid? SourceUuid,
    Guid? SourceSetUuid,
    Guid? ShowUuid
);

public sealed record NormalizationArtistExtractionCounts(
    Guid ArtistUuid,
    long Sources,
    long Tracks,
    long CreditMentions,
    long TitleSegments,
    long UnresolvedTitles
);

/// <summary>
/// Coverage and operational receipt. Counts describe extraction activity, including
/// repeated mentions; they do not measure accepted identities or accuracy.
/// InputSha256 is absent when cancellation prevented reading the complete input.
/// </summary>
public sealed record NormalizationExtractionBatchReport(
    string State,
    string InputPath,
    string? InputSha256,
    string? RunIdentitySha256,
    JsonElement? Snapshot,
    JsonElement? SnapshotCompletion,
    long InputRecords,
    long OutputRecords,
    long ExtractedRecords,
    long ErrorCount,
    IReadOnlyDictionary<string, string> ExtractorVersions,
    IReadOnlyDictionary<string, long> RecordKinds,
    IReadOnlyDictionary<string, long> UnsupportedKinds,
    IReadOnlyDictionary<string, long> CreditRoles,
    IReadOnlyDictionary<string, long> MissingTrackContext,
    IReadOnlyDictionary<long, NormalizationArtistExtractionCounts> Artists,
    IReadOnlyDictionary<string, long> ErrorReasons
);
