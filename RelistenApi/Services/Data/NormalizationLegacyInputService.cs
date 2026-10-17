using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

/// <summary>
/// Retains bounded catalog recording snapshots without provider HTTP. Historical seeding preserves
/// incomplete relationships; refreshes pair current facts with catalog revision fences. Neither
/// operation establishes provider provenance or playability.
/// </summary>
public sealed partial class NormalizationLegacyInputService(
    DbService db,
    NormalizationEvidenceService evidence
)
{
    public const string CaptureVersion = "legacy-recording-snapshot-v1";
    private const int MaximumTracks = 10_000;
    private const int MaximumSets = 1_000;
    private const int MaximumPayloadBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Retains one bounded, consistent recording snapshot only when its resource has no current input.
    /// Missing sources return Missing. Oversized snapshots fail without truncation or capture.
    /// Call outside catalog transactions; evidence must survive a later catalog rollback.
    /// </summary>
    public async Task<NormalizationLegacySeedResult> SeedRecordingAsync(
        Guid sourceUuid,
        CancellationToken ct = default
    )
    {
        if (sourceUuid == Guid.Empty)
        {
            throw new ArgumentException("A catalog source UUID is required.", nameof(sourceUuid));
        }
        if (Transaction.Current != null)
        {
            throw new InvalidOperationException(
                "Seed legacy evidence outside the catalog transaction."
            );
        }

        ct.ThrowIfCancellationRequested();
        var resource = new NormalizationResourceKey(
            "legacy_catalog",
            "recording",
            sourceUuid.ToString("D")
        );
        var current = await evidence.GetCurrentInputAsync(resource, ct);
        if (current != null)
        {
            return new(NormalizationLegacySeedDisposition.Existing, current);
        }

        var snapshot = await ReadSnapshotAsync(sourceUuid, stableProjection: false, ct);
        if (snapshot == null)
        {
            return new(NormalizationLegacySeedDisposition.Missing, null);
        }
        RequireSnapshotBounds(snapshot);

        // No catalog transaction remains open. Zero-only admission prevents this historical fallback
        // from replacing any seed or newer evidence that arrived while the snapshot was read.
        var retained = await evidence.CaptureInputAsync(
            new(
                Guid.NewGuid(),
                resource,
                Encoding.UTF8.GetBytes(snapshot.Payload!),
                "application/json; charset=utf-8",
                CaptureVersion,
                0,
                new DateTimeOffset(snapshot.SnapshotAt)
            ),
            ct
        );
        current =
            await evidence.GetCurrentInputAsync(resource, ct)
            ?? throw new InvalidOperationException(
                "Retained legacy evidence has no current input."
            );
        return new(
            retained.Fetch.AcceptedAsCurrent
                ? NormalizationLegacySeedDisposition.Seeded
                : NormalizationLegacySeedDisposition.Existing,
            current
        );
    }
}
