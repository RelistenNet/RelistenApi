using System;
using System.Linq;

namespace Relisten.Ingestion.Providers.ArchiveCollections;

/// <summary>Indexes one configured Archive selector. A page limit leaves a durable scan ready to resume.</summary>
public sealed record DiscoverArchiveCollection(Guid SelectorUuid, int? MaxPages = null);

/// <summary>
/// PostgreSQL owns this checkpoint. Pending identifiers were committed with their memberships
/// before the cursor advanced; they must settle before another page is fetched.
/// </summary>
public sealed record ArchiveCollectionDiscoveryCheckpoint(
    Guid SelectorUuid,
    Guid ScanId,
    string CollectionIdentifier,
    bool Enabled,
    string? Cursor,
    string[] PendingItems,
    long Discovered,
    bool SearchComplete,
    bool Completed
);

/// <summary>Discovery links existing sources only; an unresolved result never creates a catalog artist or recording.</summary>
public sealed record ArchiveCollectionObservationResult(ArchiveItemId Item, bool Linked);

/// <summary>Counts processing outcomes, rather than inserted rows. Failure details are bounded to twenty items.</summary>
public sealed record ArchiveCollectionDiscoverySummary(
    long Discovered = 0,
    int Observed = 0,
    int Linked = 0,
    int Unresolved = 0,
    int Failed = 0,
    bool Limited = false,
    bool Completed = false,
    bool Paused = false,
    ItemFailure[]? Failures = null
)
{
    public ArchiveCollectionDiscoverySummary Add(ArchiveCollectionObservationResult result) =>
        this with
        {
            Observed = Observed + 1,
            Linked = Linked + (result.Linked ? 1 : 0),
            Unresolved = Unresolved + (result.Linked ? 0 : 1),
        };

    public ArchiveCollectionDiscoverySummary AddFailure(ItemFailure failure) =>
        this with
        {
            Failed = Failed + 1,
            Failures = (Failures ?? []).Append(failure).Take(20).ToArray(),
        };
}

/// <summary>Continuation state carries settled workflow counts; item and cursor recovery remains in PostgreSQL.</summary>
public sealed record ArchiveCollectionDiscoveryProgress(
    IngestionExecution Execution,
    ArchiveCollectionDiscoveryCheckpoint? Checkpoint = null,
    ArchiveCollectionDiscoverySummary? Summary = null,
    int FetchedPages = 0
);
