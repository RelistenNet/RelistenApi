using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Relisten.Normalization.Venues;
using Temporalio.Activities;

namespace Relisten.Normalization.Temporal;

/// MayRetry: a second round for left-out spellings fits the run's budget. Parallel: requests
/// asked at once.
public sealed record VenueChunkInput(
    IReadOnlyList<Guid>? Artists,
    IReadOnlyList<string> Skip,
    bool MayRetry = true,
    int Parallel = 1
);

public sealed record VenueForgetInput(
    IReadOnlyList<Guid>? Artists,
    IReadOnlyList<string> Reask,
    bool Rematch
);

/// The venue catalogue's steps. Each reads the catalogue fresh, so a resumed run continues
/// from what earlier steps committed.
public sealed class VenueActivities(VenueMatch match, VenueLlm llm, VenueHistory history)
{
    [Activity("Venues.History")]
    public async Task<VenueHistoryResult> History(int minShows, IReadOnlyList<Guid>? artists)
    {
        using var heartbeat = new ActivityHeartbeat();
        return await history.RunChunkAsync(
            minShows,
            ActivityExecutionContext.Current.CancellationToken,
            artists
        );
    }

    [Activity("Venues.Match")]
    public async Task<VenueMatchResult> Match(IReadOnlyList<Guid>? artists)
    {
        using var heartbeat = new ActivityHeartbeat();
        return await match.RunAsync(artists, ActivityExecutionContext.Current.CancellationToken);
    }

    /// A re-ask or rematch run's first step: choose the placements to forget (reads only).
    [Activity("Venues.PlanForget")]
    public async Task<VenueForgetPlan> PlanForget(VenueForgetInput input)
    {
        using var heartbeat = new ActivityHeartbeat();
        return await match.PlanForgetAsync(
            input.Artists,
            input.Reask,
            input.Rematch,
            ActivityExecutionContext.Current.CancellationToken
        );
    }

    /// Its second: forget them, the re-asked ones as a re-ask (a retry forgets nothing more).
    [Activity("Venues.Forget")]
    public async Task<int> Forget(IReadOnlyList<Guid> rows, bool reask)
    {
        using var heartbeat = new ActivityHeartbeat();
        return await match.ForgetAsync(
            rows,
            reask,
            ActivityExecutionContext.Current.CancellationToken
        );
    }

    /// Its last: the venues the forgotten rows left with nothing.
    [Activity("Venues.Retire")]
    public async Task<int> Retire(IReadOnlyList<VenueId> venues)
    {
        using var heartbeat = new ActivityHeartbeat();
        return await match.RetireAsync(venues, ActivityExecutionContext.Current.CancellationToken);
    }

    [Activity("Venues.Chunk")]
    public async Task<VenueChunkResult> Chunk(VenueChunkInput input)
    {
        using var heartbeat = new ActivityHeartbeat();
        return await llm.RunChunkAsync(
            input.Artists,
            input.Skip,
            ActivityExecutionContext.Current.CancellationToken,
            input.MayRetry,
            input.Parallel
        );
    }
}

/// The venue steps that never call the model, so they run without OpenAI settings: the match
/// after an artist's import, and the nightly run's artists.
public sealed class VenueMatchActivities(VenueMatch match, NormFlags flags)
{
    /// After an artist's import: the free match for its new rows, when the artist's venues
    /// are switched on (features.norm_venues); null when they aren't.
    [Activity("Venues.MatchArtist")]
    public async Task<VenueMatchResult?> MatchArtist(Guid artist)
    {
        if (!await flags.VenuesOnAsync(artist))
        {
            return null;
        }

        // Best effort: a venue step holding the lock means defer, never keep the import open.
        using var heartbeat = new ActivityHeartbeat();
        return await match.RunAsync(
            [artist],
            ActivityExecutionContext.Current.CancellationToken,
            wait: false
        );
    }

    /// The artists whose venues are switched on, for the nightly run.
    [Activity("Venues.FlaggedArtists")]
    public Task<IReadOnlyList<Guid>> FlaggedArtists() => flags.VenueArtistsAsync();
}
