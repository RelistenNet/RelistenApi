using System;
using System.Collections.Generic;

namespace Relisten.Api.Models.Normalization;

public enum MusicBrainzLookupKind
{
    Work,
    Recording,
    Artist,
}

public enum MusicBrainzCacheMode
{
    ReuseFresh,
    Refresh,
}

public enum MusicBrainzAcquisitionState
{
    Pending,
    Completed,
    Failed,
}

public enum MusicBrainzAcquisitionDisposition
{
    Cached,
    Captured,
    Failed,
}

public sealed record MusicBrainzLookupRequest(
    MusicBrainzLookupKind Kind,
    Guid Mbid,
    MusicBrainzCacheMode CacheMode,
    TimeSpan? MaximumCacheAge = null
);

/// <summary>The original capture reference and clock, not an assurance that this input is still current.</summary>
public sealed record MusicBrainzAcquisitionResult(
    MusicBrainzAcquisitionDisposition Disposition,
    NormalizationBindingInputReference? Input = null,
    Guid? FetchUuid = null,
    long? ResourceGeneration = null,
    string? Reason = null
);

public sealed record MusicBrainzAcquisitionAttempt(
    Guid AttemptUuid,
    int Ordinal,
    string Phase,
    long ExpectedGeneration,
    DateTimeOffset HttpDeadline,
    DateTimeOffset LeaseUntil,
    string? Reason
);

public sealed record MusicBrainzAcquisition(
    Guid CommandUuid,
    MusicBrainzLookupRequest Request,
    string AcquisitionVersion,
    MusicBrainzAcquisitionState State,
    DateTimeOffset? NotBefore,
    MusicBrainzAcquisitionResult? Result,
    IReadOnlyList<MusicBrainzAcquisitionAttempt> Attempts
);

/// <summary>HTTP facts for one attempt; body is absent when an exact complete representation was unavailable.</summary>
public sealed record MusicBrainzHttpResponse(
    int? StatusCode,
    string? FinalUrl,
    IReadOnlyDictionary<string, string[]> Headers,
    byte[]? Body,
    bool BodyComplete,
    DateTimeOffset CompletedAt,
    string? Reason = null
);

public sealed record MusicBrainzSavedResponse(
    Guid AttemptUuid,
    MusicBrainzHttpResponse Response,
    string? Sha256,
    bool Eligible
);

/// <summary>Only bounded HTTP policy is configurable; supported origins and profiles are fixed.</summary>
public sealed class MusicBrainzAcquisitionOptions
{
    public string UserAgent { get; set; } = "RelistenNormalization/1.0 (https://relisten.net)";
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan SendWindow { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(45);
}

internal static class MusicBrainzAcquisitionLimits
{
    internal const int MaximumAttempts = 3;
    internal const int MaximumResponseBytes = 8 * 1024 * 1024;
    internal const int MaximumMetadataBytes = 64 * 1024;
    internal static readonly TimeSpan MinimumGap = TimeSpan.FromMilliseconds(1250);
}

internal sealed record MusicBrainzRequestProfile(
    NormalizationResourceKey Resource,
    string CaptureVersion,
    string RequestUrl
);

internal sealed record MusicBrainzDispatch(
    Guid CommandUuid,
    Guid AttemptUuid,
    Guid Token,
    MusicBrainzRequestProfile Profile,
    string UserAgent,
    long ExpectedGeneration,
    DateTimeOffset PreparedAt,
    DateTimeOffset SendDeadline,
    DateTimeOffset HttpDeadline,
    DateTimeOffset LeaseUntil
);

internal sealed record MusicBrainzAcquisitionStep(
    MusicBrainzAcquisition Acquisition,
    MusicBrainzDispatch? Dispatch = null,
    MusicBrainzSavedResponse? SavedResponse = null
);
