using System;
using Relisten.Vendor.ArchiveOrg;

namespace Relisten.Ingestion;

/// <summary>Counts each selected artist once, including work rejected by artist exclusivity.</summary>
public sealed record ImportBatchResult(
    int CompletedArtists = 0,
    int PartialArtists = 0,
    int FailedArtists = 0,
    int ConflictingArtists = 0
);

/// <summary>Mutable batch progress; the immutable artist selection travels in a separate argument.</summary>
public sealed record BatchProgress(
    IngestionExecution Execution,
    int FilterYear,
    int NextArtist = 0,
    ImportBatchResult? Result = null
);

public sealed record ArchiveCollectionCursor(string Token);

/// <summary>Validated upstream page recorded before any catalog writes.</summary>
public sealed record ArchiveCollectionPage(
    ArchiveOrgCollectionIndexItem[] Items,
    ArchiveCollectionCursor? Next
);

/// <summary>Collection discovery progress after a page has been fully applied.</summary>
public sealed record ArchiveDiscoveryProgress(
    IngestionExecution Execution,
    ArchiveCollectionCursor? Cursor = null,
    int Processed = 0
);
