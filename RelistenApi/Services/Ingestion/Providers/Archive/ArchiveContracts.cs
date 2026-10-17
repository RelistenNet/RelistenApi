using System.Text.Json.Serialization;
using Relisten.Vendor.ArchiveOrg;

namespace Relisten.Ingestion.Providers.Archive;

public sealed record ArchiveSelectedItem(SearchDoc Document, string Reason);

public sealed record ArchiveSelection(
    DiscoveredItem<ArchiveItemId, ArchiveSelectedItem>[] Items,
    ArchiveItemId[] ObservedIdentifiers
);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(DiscoveredArchiveItem), "discovered")]
[JsonDerivedType(typeof(RequestedArchiveItem), "requested")]
public abstract record ArchiveItemInput;

public sealed record DiscoveredArchiveItem(DiscoveredItem<ArchiveItemId, ArchiveSelectedItem> Item)
    : ArchiveItemInput;

public sealed record RequestedArchiveItem(ArchiveItemId Item) : ArchiveItemInput;

[JsonConverter(typeof(JsonStringEnumConverter<ArchiveSkipReason>))]
public enum ArchiveSkipReason
{
    MissingOrInvalidMetadata,
    DarkItem,
    UnrepairedDate,
    NoVbrMp3,
    UnresolvedPerformance,

    [JsonStringEnumMemberName("owned_by_taperssection")]
    OwnedByTapersSection,
}

/// <summary>
/// Reports an item outcome. For NoVbrMp3 skips, Source identifies the stored
/// recording or is null when the upstream item has not been imported.
/// </summary>
public sealed record ArchiveItemResult(
    ArchiveItemId Item,
    ItemDisposition Disposition,
    CatalogSourceId? Source,
    ArchiveSkipReason? SkipReason = null,
    string? Diagnostic = null
);

[JsonConverter(typeof(JsonStringEnumConverter<ArchiveDeletionDisposition>))]
public enum ArchiveDeletionDisposition
{
    Allowed,
    Guarded,
    NotApplicable,
}

[JsonConverter(typeof(JsonStringEnumConverter<ArchiveDeletionEvidence>))]
public enum ArchiveDeletionEvidence
{
    Present,
    Deleted,
    Unknown,
    NoVbrMp3,
}

[JsonConverter(typeof(JsonStringEnumConverter<ArchiveDeletionAction>))]
public enum ArchiveDeletionAction
{
    Keep,
    Delete,
}

public sealed record ArchiveDeletionDecision(
    ArchiveItemId Item,
    ArchiveDeletionEvidence Evidence,
    ArchiveDeletionAction Action,
    string Reason
);

public sealed record ArchiveDeletionPlan(
    ArchiveDeletionDisposition Disposition,
    ArchiveDeletionDecision[] Decisions
);

public sealed record ArchiveDeletionResult(
    ArchiveDeletionDecision[] Decisions,
    ArchiveItemId[] AbsentIdentifiers
);

public sealed record ArchiveProgress(
    ProviderStage Stage = ProviderStage.NotStarted,
    int NextItem = 0,
    ProviderSummary? Summary = null,
    ArchiveItemId[]? NoMp3 = null,
    ArchiveDeletionPlan? Deletions = null
);
