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

public sealed record NormalizationExtractionJob(
    Guid Uuid,
    decimal CapUsd,
    decimal AccountedUsd,
    int? MaximumTotalAttempts = null
);

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
)
{
    // Kept only while handling this response. Successful reusable cache entries store Json,
    // whereas a failed attempt may retain the complete bounded envelope for private diagnosis.
    internal ReadOnlyMemory<byte>? Envelope { get; init; }
}

public enum LunaCreditFailureStage
{
    ResponseEnvelope,
    CreditSchema,
    CreditSupport,
    UsageLimit,
}

/// <summary>
/// Private attempt evidence, never a reusable extraction or a public extraction result.
/// A null body means no complete bounded response was available; partial bytes are not retained.
/// </summary>
public sealed record LunaCreditFailureDiagnostic(
    LunaCreditFailureStage Stage,
    ReadOnlyMemory<byte>? ResponseBody = null
)
{
    public const int MaxResponseBodyBytes = 1_048_576;
}
