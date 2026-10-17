using System;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;
using Microsoft.Extensions.Options;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

/// <summary>
/// Captures provider evidence before a fetched value can reach catalog writes. The generation read
/// precedes HTTP, and retention uses its own transaction so later importer failures cannot erase it.
/// </summary>
public sealed class NormalizationProviderInputService(
    NormalizationEvidenceService evidence,
    IOptions<NormalizationProviderCaptureOptions> options
)
{
    /// <summary>Whether this service instance requires retained evidence before catalog projection.</summary>
    public bool Enabled { get; } = options.Value.Enabled;

    /// <summary>
    /// Runs the caller's fetch once and returns its validated value after retaining the exact response.
    /// When disabled, returns the value without reading or writing normalization evidence.
    /// </summary>
    /// <remarks>
    /// The caller owns HTTP retries. A stale response throws NormalizationStaleInputException;
    /// retry this method to read a fresh generation and issue another upstream request.
    /// EvidenceService retries retention with one fetch UUID and byte snapshot. Retention failure
    /// propagates, so the caller cannot advance its catalog checkpoint without retained evidence.
    /// No catalog transaction should be open when calling this method.
    /// </remarks>
    public async Task<T> FetchAsync<T>(
        NormalizationResourceKey resource,
        string captureVersion,
        Func<CancellationToken, Task<NormalizationProviderResponse<T>>> fetch,
        CancellationToken ct = default
    )
    {
        ct.ThrowIfCancellationRequested();
        if (!Enabled)
        {
            return (await fetch(ct)).Value;
        }

        if (Transaction.Current != null)
        {
            throw new InvalidOperationException(
                "Fetch provider input outside the catalog transaction so retained evidence survives catalog rollback."
            );
        }

        var generation = (await evidence.GetCurrentInputAsync(resource, ct))?.Generation ?? 0;
        var response = await fetch(ct);
        var capture = new NormalizationInputCapture(
            Guid.NewGuid(),
            resource,
            response.Payload.ToArray(),
            response.MediaType,
            captureVersion,
            generation,
            DateTimeOffset.UtcNow,
            response.ProviderRevision
        );
        var retained = await evidence.CaptureInputAsync(capture, ct);
        if (!retained.Fetch.AcceptedAsCurrent)
        {
            throw new NormalizationStaleInputException(resource, retained);
        }

        return response.Value;
    }
}
