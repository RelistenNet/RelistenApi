using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Relisten.Normalization.Songs;
using Temporalio.Activities;

namespace Relisten.Normalization.Temporal;

public sealed record SongCatalogueChunkInput(
    Guid Artist,
    IReadOnlyList<string> Skip,
    bool Review = false,
    IReadOnlyList<string>? Only = null
);

public sealed record SongForgetInput(Guid Artist, IReadOnlyList<string> Spellings);

/// The norm catalogue's steps. Each reads the artist's state fresh from the database, so a
/// retried or resumed step continues from what earlier steps committed.
public sealed class SongActivities(CatalogueLlm llm, CatalogueMatch match)
{
    [Activity("Songs.CatalogueChunk")]
    public async Task<CatalogueChunkResult> CatalogueChunk(SongCatalogueChunkInput input)
    {
        using var heartbeat = new ActivityHeartbeat();
        return await llm.RunChunkAsync(
            input.Artist,
            input.Skip,
            ActivityExecutionContext.Current.CancellationToken,
            input.Review,
            input.Only
        );
    }

    /// The first step of a targeted re-ask: forget the machine's decisions about these
    /// spellings so the passes after it ask again.
    [Activity("Songs.ForgetDecisions")]
    public async Task<int> ForgetDecisions(SongForgetInput input)
    {
        using var heartbeat = new ActivityHeartbeat();
        var (_, tracks) = await llm.ForgetAsync(
            input.Artist,
            input.Spellings,
            ActivityExecutionContext.Current.CancellationToken
        );
        return tracks;
    }

    [Activity("Songs.MatchTracks")]
    public async Task<CatalogueMatchResult> MatchTracks(Guid artist)
    {
        using var heartbeat = new ActivityHeartbeat();
        return await match.RunAsync(artist, ActivityExecutionContext.Current.CancellationToken);
    }
}
