using System;

namespace Relisten.Api.Models.Normalization;

/// <summary>Provider-qualified upstream identity; collection and artist membership do not duplicate inputs.</summary>
public sealed record NormalizationResourceKey(
    string Provider,
    string ResourceType,
    string ProviderKey
);

/// <summary>
/// One completed fetch. Read ExpectedGeneration before fetching upstream: only that generation
/// can become current. Reuse FetchUuid for retries of the exact same request, not for another fetch.
/// </summary>
public sealed record NormalizationInputCapture(
    Guid FetchUuid,
    NormalizationResourceKey Resource,
    byte[] Payload,
    string MediaType,
    string CaptureVersion,
    long ExpectedGeneration,
    DateTimeOffset ObservedAt,
    string? ProviderRevision = null
);

/// <summary>
/// Immutable retained bytes. Provider revision and observation time belong to individual fetches,
/// because equal bytes can be fetched repeatedly and can recur after a different revision.
/// </summary>
public sealed class NormalizationInputRevision
{
    public Guid Uuid { get; init; }
    public NormalizationResourceKey Resource { get; init; } = null!;
    public byte[] Payload { get; init; } = [];
    public string Sha256 { get; init; } = null!;
    public string MediaType { get; init; } = null!;
    public string CaptureVersion { get; init; } = null!;
    public DateTime CapturedAt { get; init; }
}

/// <summary>The current input and monotonically increasing resource generation; an absent resource starts at zero.</summary>
public sealed record NormalizationCurrentInput(
    NormalizationResourceKey Resource,
    long Generation,
    NormalizationInputRevision Revision
);

/// <summary>
/// Durable receipt for a completed fetch. AcceptedAsCurrent describes its original compare-and-swap,
/// not whether its revision remains current now. AdvancedCurrent indicates changed revision bytes/format.
/// Every accepted fetch advances ResourceGeneration, including equal content, to fence older responses.
/// Sequence orders committed fetches within a resource.
/// Observation and capture timestamps are UTC.
/// </summary>
public sealed class NormalizationInputFetch
{
    public long Sequence { get; init; }
    public Guid FetchUuid { get; init; }
    public Guid InputRevisionUuid { get; init; }
    public long ExpectedGeneration { get; init; }
    public long ResourceGeneration { get; init; }
    public bool AcceptedAsCurrent { get; init; }
    public bool AdvancedCurrent { get; init; }
    public DateTime ObservedAt { get; init; }
    public string? ProviderRevision { get; init; }
    public DateTime CapturedAt { get; init; }
}

/// <summary>A replay returns the original receipt, even if later fetches changed the resource.</summary>
public sealed record NormalizationCaptureResult(
    NormalizationInputRevision Revision,
    NormalizationInputFetch Fetch
);
