using System.Threading.Tasks;
using Relisten.Normalization.Venues;
using Temporalio.Activities;

namespace Relisten.Normalization.Temporal;

/// <summary>Owns reconciliation requests and one short database refresh per activity attempt.</summary>
public sealed class ArtistVenueRefreshActivities(ArtistVenueCounts counts)
{
    [Activity("ArtistVenues.RequestReconciliation")]
    public async Task RequestReconciliation()
    {
        using var heartbeat = new ActivityHeartbeat();
        await counts.RequestAsync(null, ActivityExecutionContext.Current.CancellationToken);
    }

    [Activity("ArtistVenues.RefreshBatch")]
    public async Task<ArtistVenueRefreshResult> RefreshBatch(int batchSize)
    {
        using var heartbeat = new ActivityHeartbeat();
        return await counts.RefreshPendingAsync(
            batchSize,
            ActivityExecutionContext.Current.CancellationToken
        );
    }
}
