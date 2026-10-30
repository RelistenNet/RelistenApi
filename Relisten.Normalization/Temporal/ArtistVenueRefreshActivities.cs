using System.Threading.Tasks;
using Relisten.Normalization.Venues;
using Temporalio.Activities;

namespace Relisten.Normalization.Temporal;

/// <summary>Runs a full count reconciliation without provider or model calls.</summary>
public sealed class ArtistVenueRefreshActivities(ArtistVenueCounts counts)
{
    [Activity("ArtistVenues.RefreshAll")]
    public async Task<ArtistVenueRefreshResult> RefreshAll()
    {
        using var heartbeat = new ActivityHeartbeat();
        return await counts.RefreshAllAsync(ActivityExecutionContext.Current.CancellationToken);
    }
}
