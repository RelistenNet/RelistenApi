namespace Relisten.Normalization.Batch;

/// <summary>Original completed Local root/directory captures and their retained raw catalog.</summary>
public sealed record RetainedLocalProviderInputsRequest(
    string CatalogManifestPath,
    string RootCaptureDirectory,
    string DirectoryCaptureDirectory
);

/// <summary>One supporting root row and one provider row per received directory, including unresolved bindings.</summary>
public sealed record RetainedLocalProviderInputsResult(
    string InputsPath,
    string DescriptorPath,
    int DirectoryRows,
    int BoundDirectories,
    int DistinctBoundSources,
    int UnresolvedDirectories
);

/// <summary>A retained artist mapping, without a current registry generation or publication fence.</summary>
public sealed record RetainedLocalArtistMapping(long ArtistId, Guid ArtistUuid, string Root);

/// <summary>
/// One provider file's exact catalog URL lookup. A bound anchor retains the native UUIDs and
/// original shard coordinate. Missing, duplicate and conflicting files remain visible.
/// </summary>
public sealed record RetainedLocalFileAnchor(
    int FileOrdinal,
    string? MediaUrl,
    string Status,
    string? Reason,
    Guid? TrackUuid = null,
    Guid? SourceUuid = null,
    Guid? SourceSetUuid = null,
    string? ShardPath = null,
    long? Line = null
);

/// <summary>
/// Historical binding through exact media ownership. ExpectedSourceUuid is a sampling hint;
/// SelectionMismatch records a difference without overriding a clear media association.
/// Original catalog rows remain in the retained shards identified by CatalogManifest.
/// </summary>
public sealed record RetainedLocalCatalogBinding(
    string Status,
    string? Reason,
    Guid? ExpectedSourceUuid,
    bool SelectionMismatch,
    Guid? SourceUuid,
    Guid? ShowUuid,
    string? ShowReason,
    IReadOnlyList<Guid> CandidateSourceUuids,
    IReadOnlyList<RetainedLocalArtistMapping> RootArtists,
    IReadOnlyList<RetainedLocalFileAnchor> Files,
    RetainedHttpArtifact CatalogManifest,
    string? SourceShardPath,
    long? SourceLine
);

/// <summary>
/// Exact retained pair and capture artifacts for one directory. Admission time belongs to
/// this offline export; it does not invent an original fetch timestamp or current input head.
/// </summary>
public sealed record RetainedLocalProviderProvenance(
    Guid InputRevisionUuid,
    Guid RootInputRevisionUuid,
    DateTimeOffset AdmittedAt,
    string Root,
    string Directory,
    string RequestedUrl,
    RetainedHttpArtifact RootManifest,
    RetainedHttpArtifact RootBody,
    RetainedHttpArtifact DirectoryManifest,
    RetainedHttpArtifact SelectionPlan,
    RetainedHttpArtifact HttpReceipts,
    int ReceiptOrdinal,
    RetainedHttpArtifact Body,
    RetainedLocalCatalogBinding CatalogBinding
);
