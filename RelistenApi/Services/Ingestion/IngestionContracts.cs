using System;
using System.Collections.Generic;
using Relisten.Api.Models;
using Relisten.Import;

namespace Relisten.Ingestion;

/// <summary>Identifies one import and the last time its retained inputs may be used.</summary>
public record IngestionRun(string Id, DateTimeOffset Deadline)
{
    public static readonly TimeSpan MaximumDuration = TimeSpan.FromDays(7);
    public DateTimeOffset RetainUntil => Deadline.AddDays(1);
}

/// <summary>An artist operation. The workflow assigns Run once and carries it through history rollover.</summary>
public record ArtistImportRequest(
    string Artist,
    string RequestId,
    UpstreamProvider? Provider = null,
    string? Item = null,
    int? Year = null,
    bool RebuildOnly = false,
    bool BackfillJerryVenues = false,
    IngestionRun? Run = null
);

/// <summary>Position in an upstream page; large discovery snapshots are delivered in small slices.</summary>
public record DiscoveryCursor(string? Upstream = null, int Offset = 0);

/// <summary>Identifies an item in an immutable discovery snapshot, not a fetched item response.</summary>
public record ImportItem(string Identifier, string Snapshot, int Index);

/// <summary>A bounded page of item references and the next discovery position.</summary>
public record ImportPage(ImportItem[] Items, DiscoveryCursor? NextCursor);

/// <summary>Resume position carried by ContinueAsNew. Item responses are never checkpointed.</summary>
public record ImportCheckpoint(
    int ProviderIndex = 0,
    DiscoveryCursor? Cursor = null,
    int ItemIndex = 0,
    bool ProviderPrepared = false,
    int FailedItems = 0,
    int ProviderFailures = 0,
    ImportStats? Stats = null,
    string[]? IneligibleItems = null,
    UpstreamProvider[]? Providers = null
);

/// <summary>The artist's enabled provider mappings in import order.</summary>
public record ArtistImportPlan(string ArtistUuid, UpstreamProvider[] Providers);

/// <summary>Committed catalog changes and whether Archive metadata proved an item ineligible.</summary>
public record ItemImportResult(ImportStats Stats, bool Ineligible = false);

/// <summary>Evidence used for an Archive deletion decision; retained in the activity result and receipt.</summary>
public record DeletionDecision(string Identifier, string Status, string Reason, bool Delete);

/// <summary>Provider finalization result, including the evidence for reconciliation decisions.</summary>
public record ProviderResult(ImportStats Stats, DeletionDecision[]? Decisions = null);

/// <summary>Final import counts. Exhausted item failures remain visible as a partial result.</summary>
public record ImportResult(string RequestId, ImportStats Stats, int FailedItems);

/// <summary>Native task queues. Provider limits apply to activities, which may issue several sequential GETs.</summary>
public static class IngestionQueues
{
    public const string Workflows = "ingest-workflows";
    public const string Database = "ingest-db";
    public static readonly IReadOnlyDictionary<UpstreamProvider, string> Providers = new Dictionary<
        UpstreamProvider,
        string
    >
    {
        [UpstreamProvider.ArchiveOrg] = "ingest-archive",
        [UpstreamProvider.Phishin] = "ingest-phishin",
        [UpstreamProvider.PhishNet] = "ingest-phishnet",
        [UpstreamProvider.SetlistFm] = "ingest-setlistfm",
        [UpstreamProvider.PhantasyTour] = "ingest-phantasytour",
        [UpstreamProvider.JerryGarcia] = "ingest-jerry",
        [UpstreamProvider.Local] = "ingest-local",
    };
}

/// <summary>Scheduled or manual batch with its stable selection and workflow rollover position.</summary>
public record ImportBatchRequest(
    string Mode,
    string? RequestId = null,
    int Offset = 0,
    int Completed = 0,
    int Failed = 0,
    IngestionRun? Run = null
);

/// <summary>A bounded slice of the selected artist UUIDs.</summary>
public record ArtistBatchPage(string[] Artists, int? NextOffset);

/// <summary>Batch counts; failed includes an artist already owned by another active import.</summary>
public record ImportBatchResult(int Completed, int Failed);

/// <summary>Result of indexing one Archive collection page.</summary>
public record DiscoveryPage(string? NextCursor, int Count);
