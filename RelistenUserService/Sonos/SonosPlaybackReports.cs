using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Relisten.Identifiers;
using RelistenUserService.History;
using RelistenUserService.Persistence;

namespace RelistenUserService.Sonos;

public sealed class SonosPlaybackReports(
    AccountsDbContext database,
    PlayHistoryUploads history,
    TimeProvider clock
)
{
    private const int FourMinutesMs = 4 * 60 * 1000;

    internal async Task RecordAsync(SonosQueue queue, JsonElement report, CancellationToken ct)
    {
        var plays = ReadQualifiedPlays(queue, report);
        if (plays.Count == 0)
        {
            return;
        }

        await using var transaction = await database.Database.BeginTransactionAsync(ct);
        var claimed = await ClaimAsync(queue.Id, plays.Keys.ToArray(), ct);
        if (claimed.Length > 0)
        {
            await history.WriteSonosAsync(
                queue.UserId,
                clock.GetUtcNow(),
                claimed.Select(id => plays[id]),
                ct
            );
        }
        await transaction.CommitAsync(ct);
    }

    private async Task<Guid[]> ClaimAsync(Guid queueId, Guid[] reports, CancellationToken ct) =>
        await database
            .Database.SqlQuery<Guid>(
                $"""
                WITH claimed AS (
                    INSERT INTO user_data.sonos_playback_reports (queue_id, report_id)
                    SELECT {queueId}, report_id FROM unnest({reports}) AS report_id ORDER BY report_id
                    ON CONFLICT DO NOTHING RETURNING report_id
                )
                SELECT report_id AS "Value" FROM claimed
                """
            )
            .ToArrayAsync(ct);

    private static Dictionary<Guid, PlayHistoryUploads.SonosPlay> ReadQualifiedPlays(
        SonosQueue queue,
        JsonElement report
    )
    {
        if (
            report.ValueKind != JsonValueKind.Object
            || !report.TryGetProperty("items", out var entries)
            || entries.ValueKind != JsonValueKind.Array
        )
        {
            return [];
        }

        var items = queue
            .ReadItems()
            .Concat(queue.ReadRemovedItems().Select(row => row.Item))
            .ToDictionary(row => row.Id, StringComparer.Ordinal);
        var plays = new Dictionary<Guid, PlayHistoryUploads.SonosPlay>();
        foreach (var entry in entries.EnumerateArray())
        {
            if (
                ReadItem(entry) is not { } reading
                || !items.TryGetValue(reading.ItemId, out var item)
                || reading.PlayedMillis < QualificationMillis(item)
                || !TrackId.TryParse(item.ResolvedTrackId, out var track)
            )
            {
                continue;
            }

            plays.TryAdd(
                reading.ReportId,
                new(
                    track,
                    item.PlaylistId,
                    item.PlaylistEntryId,
                    item.Track.DurationMillis,
                    reading.PlayedMillis
                )
            );
        }
        return plays;
    }

    private static ReportedItem? ReadItem(JsonElement entry)
    {
        if (
            entry.ValueKind != JsonValueKind.Object
            || !entry.TryGetProperty("reportId", out var id)
            || id.ValueKind != JsonValueKind.String
            || !id.TryGetGuid(out var reportId)
            || reportId == Guid.Empty
            || !entry.TryGetProperty("id", out var itemId)
            || itemId.ValueKind != JsonValueKind.String
            || !entry.TryGetProperty("durationPlayedMillis", out var duration)
            || duration.ValueKind != JsonValueKind.Number
            || !duration.TryGetInt64(out var playedMillis)
        )
        {
            return null;
        }

        return new(reportId, itemId.GetString()!, playedMillis);
    }

    private sealed record ReportedItem(Guid ReportId, string ItemId, long PlayedMillis);

    private static int QualificationMillis(QueueTrack item) =>
        item.Track.DurationMillis is > 0 and var duration
            ? Math.Min(FourMinutesMs, duration / 2 + duration % 2)
            : FourMinutesMs;
}
