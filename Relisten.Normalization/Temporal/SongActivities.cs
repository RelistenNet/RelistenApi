using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Npgsql;
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
public sealed class SongActivities(
    CatalogueLlm llm,
    CatalogueMatch match,
    SongCounts counts,
    ILogger<SongActivities> logger
)
{
    [Activity("Songs.CatalogueChunk")]
    public async Task<CatalogueChunkResult> CatalogueChunk(SongCatalogueChunkInput input)
    {
        using var heartbeat = new ActivityHeartbeat();
        var result = await llm.RunChunkAsync(
            input.Artist,
            input.Skip,
            ActivityExecutionContext.Current.CancellationToken,
            input.Review,
            input.Only
        );
        await RefreshCountsAsync(input.Artist);
        return result;
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
        await RefreshCountsAsync(input.Artist);
        return tracks;
    }

    [Activity("Songs.MatchTracks")]
    public async Task<CatalogueMatchResult> MatchTracks(Guid artist)
    {
        using var heartbeat = new ActivityHeartbeat();
        var result = await match.RunAsync(
            artist,
            ActivityExecutionContext.Current.CancellationToken
        );
        await RefreshCountsAsync(artist);
        return result;
    }

    private async Task RefreshCountsAsync(Guid artist)
    {
        var cancellationToken = ActivityExecutionContext.Current.CancellationToken;
        try
        {
            await counts.RefreshArtistsAsync([artist], cancellationToken);
        }
        catch (Exception exception)
            when (!cancellationToken.IsCancellationRequested
                && exception is NpgsqlException or TimeoutException
            )
        {
            // Source work already committed. Do not repeat a paid model call because
            // a derived recount failed; an operator can refresh this artist explicitly.
            logger.LogWarning(
                exception,
                "Song count refresh failed after normalizing {Artist}; rerun the refresh explicitly",
                artist
            );
        }
    }
}
