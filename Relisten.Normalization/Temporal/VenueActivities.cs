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

/// The venue catalogue's steps. Each reads the catalogue fresh, so a resumed run continues
/// from what earlier steps committed.
public sealed class VenueActivities(VenueMatch match, VenueLlm llm, VenueHistory history)
{
    [Activity("Venues.History")]
    public async Task<VenueHistoryResult> History(int minShows)
    {
        using var heartbeat = new ActivityHeartbeat();
        return await history.RunChunkAsync(
            minShows,
            ActivityExecutionContext.Current.CancellationToken
        );
    }

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

        using var heartbeat = new ActivityHeartbeat();
        return await match.RunAsync([artist], ActivityExecutionContext.Current.CancellationToken);
    }

    /// The artists whose venues are switched on, for the nightly run.
    [Activity("Venues.FlaggedArtists")]
    public Task<IReadOnlyList<Guid>> FlaggedArtists() => flags.VenueArtistsAsync();
}
