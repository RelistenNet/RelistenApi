using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Relisten.Api.Models;

namespace Relisten.Ingestion;

/// <summary>Correlation and the absolute lifetime shared by all runs and children of an operation.</summary>
public sealed record IngestionExecution(string Id, DateTimeOffset Deadline)
{
    public static readonly TimeSpan MaximumDuration = TimeSpan.FromDays(7);
}

public sealed record ProviderRequest<T>(T Request, IngestionExecution Execution);

public sealed record ProviderWorkflowContext(ArtistId Artist, IngestionExecution Execution);

public sealed record DiscoveredItem<TKey, TInput>(TKey Id, TInput Input);

public sealed record Page<TItem, TCursor>(TItem[] Items, TCursor? Next)
    where TCursor : class;

[JsonConverter(typeof(JsonStringEnumConverter<ItemDisposition>))]
public enum ItemDisposition
{
    Applied,
    Unchanged,
    Skipped,
}

[JsonConverter(typeof(JsonStringEnumConverter<ProviderStage>))]
public enum ProviderStage
{
    NotStarted,
    Importing,
    Finalizing,
    Complete,
}

[JsonConverter(typeof(JsonStringEnumConverter<ImportStatus>))]
public enum ImportStatus
{
    Completed,
    Partial,
}

/// <summary>A bounded failure sample. Keys retain their provider-specific wire type.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ItemFailure<ArchiveItemId>), "archive")]
[JsonDerivedType(typeof(ItemFailure<PhishInShowId>), "phish-in")]
[JsonDerivedType(typeof(ItemFailure<CatalogSourceId>), "source")]
[JsonDerivedType(typeof(ItemFailure<SetlistFmId>), "setlist-fm")]
[JsonDerivedType(typeof(ItemFailure<PhantasyTourShowId>), "phantasy-tour")]
[JsonDerivedType(typeof(ItemFailure<JerryShowId>), "jerry")]
[JsonDerivedType(typeof(ItemFailure<LocalShowDate>), "local")]
public abstract record ItemFailure(string Category, string Message);

public sealed record ItemFailure<TKey>(TKey Key, string Category, string Message)
    : ItemFailure(Category, Message);

public sealed record ProviderSummary(
    int CompletedItems = 0,
    int SkippedItems = 0,
    int FailedItems = 0,
    ItemFailure[]? Failures = null
)
{
    public ProviderSummary Add(ItemDisposition disposition) =>
        disposition == ItemDisposition.Skipped
            ? this with
            {
                SkippedItems = SkippedItems + 1,
            }
            : this with
            {
                CompletedItems = CompletedItems + 1,
            };

    public ProviderSummary AddFailure(ItemFailure failure)
    {
        var failures = new List<ItemFailure>(Failures ?? []);
        if (failures.Count < 20)
            failures.Add(failure);
        return this with { FailedItems = FailedItems + 1, Failures = failures.ToArray() };
    }
}

public sealed record ArtistImportResult(
    ArtistId Artist,
    int CompletedItems,
    int SkippedItems,
    int FailedItems
)
{
    public ImportStatus Status => FailedItems == 0 ? ImportStatus.Completed : ImportStatus.Partial;
}

public sealed record ArtistImportPlan(ArtistId Artist, UpstreamProvider[] Providers);

/// <summary>Provider activity queues retain independent concurrency and rate limits.</summary>
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
