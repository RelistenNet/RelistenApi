using System;
using System.Collections.Generic;
using Relisten.Import;

namespace Relisten.Ingestion;

/// <summary>A logical ingestion request. RequestId survives retries and Continue-As-New.</summary>
public record ArtistImportRequest(
    string Artist,
    string RequestId,
    string? Provider = null,
    string? Item = null,
    int? Year = null,
    bool RebuildOnly = false,
    bool BackfillJerryVenues = false
);

/// <summary>Small workflow checkpoint. Upstream payloads stay in application storage.</summary>
public record ImportCheckpoint(
    int ProviderIndex = 0,
    string? Cursor = null,
    int ItemIndex = 0,
    int FailedItems = 0,
    ImportStats? Stats = null
);

/// <summary>A page of staged work; item bodies are addressed by page and index.</summary>
public record ImportPage(string Key, int ItemCount, string? NextCursor);

/// <summary>Reference to one provider item, independent of a Temporal execution attempt.</summary>
public record ImportItemRef(ArtistImportRequest Request, string Provider, string Page, int Index);

/// <summary>Artist identity and ordered active providers resolved from the catalog.</summary>
public record ArtistImportPlan(string ArtistUuid, string[] Providers);

/// <summary>Compact final result; failures remain visible even when other items succeeded.</summary>
public record ImportResult(string RequestId, ImportStats Stats, int FailedItems);

/// <summary>Task queues shared by the API client and worker. PanicStream is intentionally absent.</summary>
public static class IngestionQueues
{
    public const string Workflows = "ingest-workflows";
    public const string Database = "ingest-db";
    public static readonly IReadOnlyDictionary<string, string> Providers = new Dictionary<
        string,
        string
    >
    {
        ["archive.org"] = "ingest-archive",
        ["phish.in"] = "ingest-phishin",
        ["phish.net"] = "ingest-phishnet",
        ["setlist.fm"] = "ingest-setlistfm",
        ["phantasytour.com"] = "ingest-phantasytour",
        ["jerrygarcia.com"] = "ingest-jerry",
        ["local"] = "ingest-local",
    };
}

/// <summary>A scheduled or manual catalog operation; a request ID identifies its stable artist selection.</summary>
public record ImportBatchRequest(
    string Mode,
    string? RequestId = null,
    int Offset = 0,
    int Completed = 0,
    int Failed = 0
);

/// <summary>Bounded artist identifiers for a batch run.</summary>
public record ArtistBatchPage(string[] Artists, int? NextOffset);

/// <summary>Batch summary including artist workflows that failed or were already running.</summary>
public record ImportBatchResult(int Completed, int Failed);

/// <summary>Cursor-only result of one Archive collection-discovery page.</summary>
public record DiscoveryPage(string? NextCursor, int Count);
