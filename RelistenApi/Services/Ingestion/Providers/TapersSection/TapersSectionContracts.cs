using System;
using System.Linq;
using System.Text.Json.Serialization;

namespace Relisten.Ingestion.Providers.TapersSection;

/// <summary>Discovers item identifiers before observing metadata; a limit is a pilot, not a complete scan.</summary>
public sealed record DiscoverTapersSection(int? MaxItems = null);

/// <summary>Materializes approved candidates and refreshes approved members plus paused owners of dirty revisions.</summary>
public sealed record RefreshTapersSection;

/// <summary>Refreshes retained items for one mapped cohort artist under the ordinary artist operation lock.</summary>
public sealed record RefreshTapersSectionArtist(ArtistId Artist);

/// <summary>Pins the expected candidate from selection; publication rechecks that assignment under row locks.</summary>
public sealed record TapersSectionItemInput(ArchiveItemId Item, Guid CandidateId);

[JsonConverter(typeof(JsonStringEnumConverter<TapersSectionItemOutcome>))]
public enum TapersSectionItemOutcome
{
    [JsonStringEnumMemberName("published")]
    Published,

    [JsonStringEnumMemberName("unchanged")]
    Unchanged,

    [JsonStringEnumMemberName("deferred")]
    Deferred,

    [JsonStringEnumMemberName("needs_review")]
    NeedsReview,

    [JsonStringEnumMemberName("paused")]
    Paused,

    [JsonStringEnumMemberName("unavailable")]
    Unavailable,

    [JsonStringEnumMemberName("failed")]
    Failed,
}

/// <summary>
/// A returned result confirms retained evidence, including held publication. Exhausted activity
/// failures are counted separately; their retained evidence remains available in database status.
/// </summary>
public sealed record TapersSectionItemResult(
    ArchiveItemId Item,
    TapersSectionItemOutcome Outcome,
    Guid? SourceUuid = null,
    TapersSectionReason? Reason = null,
    bool Observed = true
);

/// <summary>Counts outcomes, not inserted rows. Failures retain at most twenty item details.</summary>
public sealed record TapersSectionSummary(
    int Discovered = 0,
    int Observed = 0,
    int Published = 0,
    int Unchanged = 0,
    int Deferred = 0,
    int NeedsReview = 0,
    int Paused = 0,
    int Unavailable = 0,
    int Failed = 0,
    int Conflicts = 0,
    bool Limited = false,
    ItemFailure[]? Failures = null
)
{
    public TapersSectionSummary Add(TapersSectionItemResult item) =>
        this with
        {
            Observed = Observed + (item.Observed ? 1 : 0),
            Published = Published + (item.Outcome == TapersSectionItemOutcome.Published ? 1 : 0),
            Unchanged = Unchanged + (item.Outcome == TapersSectionItemOutcome.Unchanged ? 1 : 0),
            Deferred = Deferred + (item.Outcome == TapersSectionItemOutcome.Deferred ? 1 : 0),
            NeedsReview =
                NeedsReview + (item.Outcome == TapersSectionItemOutcome.NeedsReview ? 1 : 0),
            Paused = Paused + (item.Outcome == TapersSectionItemOutcome.Paused ? 1 : 0),
            Unavailable =
                Unavailable + (item.Outcome == TapersSectionItemOutcome.Unavailable ? 1 : 0),
            Failed = Failed + (item.Outcome == TapersSectionItemOutcome.Failed ? 1 : 0),
        };

    public TapersSectionSummary AddFailure(ItemFailure failure) =>
        this with
        {
            Failed = Failed + 1,
            Failures = (Failures ?? []).Append(failure).Take(20).ToArray(),
        };

    public TapersSectionSummary Add(TapersSectionSummary other) =>
        this with
        {
            Discovered = Discovered + other.Discovered,
            Observed = Observed + other.Observed,
            Published = Published + other.Published,
            Unchanged = Unchanged + other.Unchanged,
            Deferred = Deferred + other.Deferred,
            NeedsReview = NeedsReview + other.NeedsReview,
            Paused = Paused + other.Paused,
            Unavailable = Unavailable + other.Unavailable,
            Failed = Failed + other.Failed,
            Conflicts = Conflicts + other.Conflicts,
            Limited = Limited || other.Limited,
            Failures = (Failures ?? []).Concat(other.Failures ?? []).Take(20).ToArray(),
        };
}

/// <summary>Reports item outcomes and successful completion of shows, years, cache invalidation and revision acknowledgement.</summary>
public sealed record TapersSectionArtistResult(
    ArtistId Artist,
    TapersSectionSummary Summary,
    bool RebuildCompleted
);

/// <summary>A committed item revision captured before rebuilding; later revisions remain pending.</summary>
public sealed record TapersSectionDirtyRevision(ArchiveItemId Item, long Revision);

/// <summary>Only settled work crosses continuation runs. Seen IDs reject a nonadvancing search traversal.</summary>
public sealed record TapersSectionDiscoveryProgress(
    IngestionExecution Execution,
    string? Cursor = null,
    string[]? SeenItems = null,
    TapersSectionSummary? Summary = null,
    ArchiveItemId[]? PendingObservations = null,
    int NextObservation = 0,
    bool SearchComplete = false,
    bool RecoverySelected = false,
    string[]? SeenCursors = null
);

public sealed record TapersSectionRefreshProgress(
    IngestionExecution Execution,
    int NextArtist = 0,
    TapersSectionSummary? Summary = null
);

public sealed record TapersSectionArtistProgress(
    IngestionExecution Execution,
    int NextItem = 0,
    TapersSectionSummary? Summary = null
);
