using System;

namespace Relisten.Api.Models.Normalization;

/// <summary>Runtime limits for one bounded extraction attempt. Prices are USD per million tokens.</summary>
public sealed record LunaCreditExtractionOptions
{
    public string Model { get; init; } = "gpt-6-luna";
    public int MaxOutputTokens { get; init; } = 2048;
    public int MaxAttempts { get; init; } = 2;
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(90);
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(3);
    public decimal InputUsdPerMillionTokens { get; init; } = 0.10m;
    public decimal OutputUsdPerMillionTokens { get; init; } = 0.50m;
}

/// <summary>Successful extraction still proposes wording and roles, never global person identities.</summary>
public sealed record LunaCreditExtractionResult(
    string State,
    RecordingCreditExtraction Extraction,
    string? CacheKey = null,
    bool FromCache = false,
    string? Reason = null
);

public sealed record NormalizationExtractionJob(Guid Uuid, decimal CapUsd, decimal AccountedUsd);

/// <summary>An admitted attempt owns its reservation even if its HTTP outcome becomes uncertain.</summary>
public sealed record NormalizationExtractionClaim(
    string State,
    string CacheKey,
    Guid? AttemptUuid = null,
    string? ResponseJson = null
);

public sealed record LunaCreditResponse(
    string Json,
    string? ProviderRequestId,
    long InputTokens,
    long OutputTokens
);
