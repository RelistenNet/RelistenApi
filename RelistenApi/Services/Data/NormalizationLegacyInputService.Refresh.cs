using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationLegacyInputService
{
    public const string RefreshCaptureVersion = "legacy-recording-snapshot-v2";

    /// <summary>
    /// Retains current recording facts with the catalog revisions read in the same primary snapshot.
    /// Returns null for a missing source. Bounds or missing recording-artist revisions fail without capture.
    /// Missing derived show, venue or file context remains retained unknown and does not withhold credits.
    /// Call outside catalog transactions; capture commits independently of later catalog operations.
    /// </summary>
    /// <remarks>
    /// An unchanged refresh reuses its content revision but advances the input generation. A competing
    /// refresh throws NormalizationStaleInputException; retry the whole refresh. The returned receipt
    /// belongs to this snapshot, never to a concurrent winner. Checked binding and work admission must
    /// consume CatalogInputs unchanged to prevent old wording from acquiring newer catalog fences.
    /// </remarks>
    public async Task<NormalizationLegacyRecordingSnapshot?> RefreshRecordingAsync(
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
                "Refresh legacy evidence outside the catalog transaction."
            );
        }

        ct.ThrowIfCancellationRequested();
        var resource = new NormalizationResourceKey(
            "legacy_catalog",
            "recording",
            sourceUuid.ToString("D")
        );
        var generation = (await evidence.GetCurrentInputAsync(resource, ct))?.Generation ?? 0;
        var snapshot = await ReadSnapshotAsync(sourceUuid, stableProjection: true, ct);
        if (snapshot == null)
        {
            return null;
        }

        // Catalog reads have committed before retention starts. Resource CAS fences competing
        // refreshes; the catalog read set separately fences changes during capture or binding.
        var retained = await evidence.CaptureInputAsync(
            new(
                Guid.NewGuid(),
                resource,
                Encoding.UTF8.GetBytes(snapshot.Payload!),
                "application/json; charset=utf-8",
                RefreshCaptureVersion,
                generation,
                new DateTimeOffset(snapshot.SnapshotAt)
            ),
            ct
        );
        if (!retained.Fetch.AcceptedAsCurrent)
        {
            throw new NormalizationStaleInputException(resource, retained);
        }

        ct.ThrowIfCancellationRequested();
        return new(retained, snapshot.CatalogInputs);
    }
}
