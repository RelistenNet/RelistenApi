using System;

namespace Relisten.Api.Models.Normalization;

/// <summary>Enables retention before provider results can enter the catalog. Disabled by default during rollout.</summary>
public sealed class NormalizationProviderCaptureOptions
{
    public bool Enabled { get; set; }
}

/// <summary>
/// One validated provider response and the exact bytes from which its value was read.
/// The caller owns HTTP handling and validation; payloads must not be reconstructed from Value.
/// </summary>
public sealed record NormalizationProviderResponse<T>(
    T Value,
    ReadOnlyMemory<byte> Payload,
    string MediaType,
    string? ProviderRevision = null
);

/// <summary>
/// Another fetch became current while this request was in flight. Its bytes remain retained,
/// but the caller must retry the entire upstream fetch before publishing its catalog projection.
/// </summary>
public sealed class NormalizationStaleInputException(
    NormalizationResourceKey resource,
    NormalizationCaptureResult capture
) : InvalidOperationException("A newer provider fetch became current. Retry the upstream request.")
{
    public NormalizationResourceKey Resource { get; } = resource;
    public NormalizationCaptureResult Capture { get; } = capture;
}
