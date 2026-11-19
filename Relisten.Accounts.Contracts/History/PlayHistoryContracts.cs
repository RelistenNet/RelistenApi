using Relisten.Accounts.Contracts.Identifiers;

namespace Relisten.Accounts.Contracts.History;

/// <summary>Keep the submitted values, including QualifiedAt, stable across retries.</summary>
public sealed record PlayUpload(
    PlayId PlayId,
    string TrackId,
    DateTimeOffset QualifiedAt,
    DateTimeOffset StartedAt,
    short AppType,
    bool Offline,
    string Origin,
    PlaylistId? PlaylistId = null,
    PlaylistEntryId? PlaylistEntryId = null,
    int? CatalogDurationMs = null
);

public sealed record PlayBatchUpload(int ContractVersion, IReadOnlyList<PlayUpload> Plays);

/// <summary>Results appear in request order. A rejected item may have no usable play ID.</summary>
public sealed record PlayBatchUploaded(
    int ContractVersion,
    IReadOnlyList<PlayUploadResult> Results
);

public sealed record PlayUploadResult(PlayId? PlayId, string Status);
