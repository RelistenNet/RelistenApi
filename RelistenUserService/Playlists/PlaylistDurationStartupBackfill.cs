namespace RelistenUserService.Playlists;

/// <summary>Repairs missing summaries after migration without delaying readiness.</summary>
internal sealed class PlaylistDurationStartupBackfill(
    IServiceScopeFactory scopes,
    ILogger<PlaylistDurationStartupBackfill> logger
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var updated = await scope
                .ServiceProvider.GetRequiredService<PlaylistDurationBackfill>()
                .RunAsync(stoppingToken);
            if (updated > 0)
            {
                logger.LogInformation(
                    "Backfilled durations for {UpdatedPlaylists} playlists",
                    updated
                );
            }
        }
        catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogError(exception, "Playlist duration backfill failed");
        }
    }
}
