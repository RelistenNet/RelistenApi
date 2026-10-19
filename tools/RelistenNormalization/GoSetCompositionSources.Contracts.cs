using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization.Batch;

/// <summary>Literal first-party pages to retain, not a song/work identity decision.</summary>
public sealed record GoSetCompositionSourcesRequest(
    IReadOnlyList<string> Urls,
    long MinimumFreeBytes = 4L * 1024 * 1024 * 1024
);

public sealed record GoSetCompositionArtifact(string Path, long Bytes, string Sha256);

public sealed record GoSetCompositionCapture(
    string RequestedUrl,
    string FinalUrl,
    int StatusCode,
    string MediaType,
    DateTimeOffset CapturedAt,
    Guid InputRevisionUuid,
    NormalizationResourceKey Resource,
    GoSetCompositionArtifact Body,
    GoSetCompositionArtifact Input,
    GoSetCompositionArtifact Decoded
);

public sealed record GoSetCompositionSourcesResult(
    string Version,
    string State,
    string RequestSha256,
    string DecoderVersion,
    IReadOnlyList<GoSetCompositionCapture> Captures
);
