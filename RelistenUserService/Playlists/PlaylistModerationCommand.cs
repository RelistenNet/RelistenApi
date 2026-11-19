using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RelistenUserService.Library;
using RelistenUserService.Persistence;

namespace RelistenUserService.Playlists;

/// <summary>One-shot database commands. Never start the HTTP host or apply migrations.</summary>
internal static class PlaylistModerationCommand
{
    internal static async Task<int> RunAsync(
        string[] args,
        Func<AccountsDbContext> openDatabase,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken
    )
    {
        if (args is ["list-reports"])
        {
            await using var database = openDatabase();
            var reports = await database
                .PlaylistReports.AsNoTracking()
                .Where(row => row.ClosedAt == null)
                .OrderBy(row => row.CreatedAt)
                .ThenBy(row => row.Id)
                .Take(100)
                .ToArrayAsync(cancellationToken);
            await output.WriteLineAsync(JsonSerializer.Serialize(reports, PlaylistJson.Options));
            return 0;
        }

        if (
            args is ["close-report", var report]
            && PlaylistReportId.TryParse(report, out var reportId)
        )
        {
            await using var database = openDatabase();
            var now = TimeProvider.System.GetUtcNow();
            var changed = await database
                .PlaylistReports.Where(row => row.Id == reportId && row.ClosedAt == null)
                .ExecuteUpdateAsync(
                    set => set.SetProperty(row => row.ClosedAt, now),
                    cancellationToken
                );
            await output.WriteLineAsync(
                changed == 1 ? "Report closed." : "No matching open report."
            );
            return 0;
        }

        if (
            args is ["take-down" or "lift-takedown", var playlist]
            && PlaylistId.TryParse(playlist, out var playlistId)
        )
        {
            await using var database = openDatabase();
            var writes = new PlaylistWriteService(
                database,
                new AdvisoryLockService(database),
                new LibraryStateStore(database),
                TimeProvider.System
            );
            try
            {
                var changed = await writes.SetTakedownAsync(
                    playlistId,
                    args[0] == "take-down",
                    cancellationToken
                );
                await output.WriteLineAsync(
                    changed
                        ? "Playlist moderation state updated."
                        : "Playlist moderation state already set."
                );
                return 0;
            }
            catch (PlaylistException exception) when (exception.Status == 404)
            {
                await error.WriteLineAsync(exception.Message);
                return 1;
            }
        }

        await error.WriteLineAsync(
            "Usage: playlist-moderation list-reports | close-report <report-id> | take-down <playlist-id> | lift-takedown <playlist-id>"
        );
        return 1;
    }
}
