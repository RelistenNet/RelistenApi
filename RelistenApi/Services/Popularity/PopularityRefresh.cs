using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Logging;

namespace Relisten.Services.Popularity;

public partial class PopularityService
{
    /// <summary>
    /// Refreshes request-time populations queued by PopularityRefreshRequest.
    /// Retries recalculate even if Postgres is fresh: an earlier attempt may have
    /// committed there before Redis publication failed.
    /// </summary>
    public async Task RefreshPopulations(
        PopularityPopulation[] populations,
        CancellationToken cancellationToken = default
    )
    {
        foreach (var population in populations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (population.Kind)
            {
                case PopularityPopulationKind.Artist:
                    await RefreshArtistPopularityMap();
                    break;
                case PopularityPopulationKind.Show when population.Artist is { } artist:
                    await RefreshShowPopularityMapForArtist(artist);
                    break;
                case PopularityPopulationKind.Show:
                    await RefreshGlobalShowPopularityMap();
                    break;
                case PopularityPopulationKind.Year when population.Artist is { } artist:
                    await RefreshYearPopularityMapForArtist(artist);
                    break;
                case PopularityPopulationKind.Year:
                    await RefreshGlobalYearPopularityMap();
                    break;
                default:
                    throw new System.ArgumentOutOfRangeException(nameof(populations));
            }
        }
    }

    /// <summary>Publishes catalog and playlist popularity independently and reports failures to the job runner.</summary>
    public async Task RefreshReadModel(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var failures = new List<Exception>();
        try
        {
            await RefreshCatalogReadModel(failures, cancellationToken);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(error, "Could not refresh catalog popularity");
            failures.Add(error);
        }

        try
        {
            await RefreshPlaylistPopularity(cancellationToken);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(error, "Could not refresh playlist popularity");
            failures.Add(error);
        }

        if (failures.Count > 0)
        {
            throw new AggregateException("Some popularity populations failed.", failures);
        }
    }

    private async Task RefreshCatalogReadModel(
        List<Exception> failures,
        CancellationToken cancellationToken
    )
    {
        await RefreshArtistPopularityMap();
        await RefreshGlobalShowPopularityMap();
        await RefreshGlobalYearPopularityMap();

        var artists = await ReadOwners(cancellationToken);
        var started = Stopwatch.StartNew();
        long performances = 0;
        long recordings = 0;
        long songs = 0;

        foreach (var artist in artists)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await RefreshShowPopularityMapForArtist(artist);
                await RefreshYearPopularityMapForArtist(artist);
                var counts = await RefreshDetailPopulations(artist, cancellationToken);
                performances += counts.Performances;
                recordings += counts.Recordings;
                songs += counts.Songs;
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    error,
                    "Could not refresh popularity for artist {Artist}",
                    artist
                );
                failures.Add(error);
            }
        }

        logger.LogInformation(
            "Refreshed popularity for {Performances} performances, {Recordings} recordings, and {Songs} songs in {Elapsed}",
            performances,
            recordings,
            songs,
            started.Elapsed
        );
    }

    private async Task<Guid[]> ReadOwners(CancellationToken cancellationToken) =>
        (
            await db.WithConnection(connection =>
                connection.QueryAsync<Guid>(
                    new CommandDefinition(
                        "SELECT uuid FROM artists ORDER BY uuid",
                        cancellationToken: cancellationToken
                    )
                )
            )
        ).ToArray();
}
