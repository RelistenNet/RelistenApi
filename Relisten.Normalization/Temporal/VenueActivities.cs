using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Relisten.Normalization.Venues;
using Temporalio.Activities;

namespace Relisten.Normalization.Temporal;

public sealed record VenueChunkInput(IReadOnlyList<Guid>? Artists, IReadOnlyList<string> Skip);

/// The venue catalogue's steps. Each reads the catalogue fresh, so a resumed run continues
/// from what earlier steps committed.
public sealed class VenueActivities(VenueMatch match, VenueLlm llm)
{
    [Activity("Venues.Match")]
    public async Task<VenueMatchResult> Match(IReadOnlyList<Guid>? artists)
    {
        using var heartbeat = new ActivityHeartbeat();
        return await match.RunAsync(artists, ActivityExecutionContext.Current.CancellationToken);
    }

    [Activity("Venues.Chunk")]
    public async Task<VenueChunkResult> Chunk(VenueChunkInput input)
    {
        using var heartbeat = new ActivityHeartbeat();
        return await llm.RunChunkAsync(
            input.Artists,
            input.Skip,
            ActivityExecutionContext.Current.CancellationToken
        );
    }
}
