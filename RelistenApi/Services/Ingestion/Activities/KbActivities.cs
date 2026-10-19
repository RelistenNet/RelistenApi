using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Relisten.Kb.Catalogue;
using Temporalio.Activities;

namespace Relisten.Ingestion;

public sealed record KbCatalogueChunkInput(
    Guid Artist,
    IReadOnlyList<string> Skip,
    bool Review = false,
    IReadOnlyList<string>? Only = null
);

public sealed record KbForgetInput(Guid Artist, IReadOnlyList<string> Spellings);

/// The kb catalogue's steps. Each reads the artist's state fresh from the database, so a
/// retried or resumed step continues from what earlier steps committed.
public sealed class KbActivities(CatalogueLlm llm, CatalogueMatch match)
{
    [Activity("Kb.CatalogueChunk")]
    public async Task<CatalogueChunkResult> CatalogueChunk(KbCatalogueChunkInput input)
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
    [Activity("Kb.ForgetDecisions")]
    public async Task<int> ForgetDecisions(KbForgetInput input)
    {
        using var heartbeat = new ActivityHeartbeat();
        var (_, tracks) = await llm.ForgetAsync(
            input.Artist,
            input.Spellings,
            ActivityExecutionContext.Current.CancellationToken
        );
        return tracks;
    }

    [Activity("Kb.MatchTracks")]
    public async Task<CatalogueMatchResult> MatchTracks(Guid artist)
    {
        using var heartbeat = new ActivityHeartbeat();
        return await match.RunAsync(artist, ActivityExecutionContext.Current.CancellationToken);
    }
}
