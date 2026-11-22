using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using Relisten.Services.Popularity;

namespace Relisten.Search;

internal static class SearchPlayTotals
{
    public static async Task<Dictionary<Guid, TrackStartTotal>> Load(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DateOnly through,
        string? exportPath,
        CancellationToken cancellationToken,
        Guid? artist = null
    )
    {
        if (exportPath == null)
        {
            var rows = await PlayRollupReader.ReadTrackStarts(
                connection,
                transaction,
                through,
                cancellationToken,
                artist
            );
            return rows.ToDictionary(row => row.Id);
        }

        return await ReadExport(exportPath, cancellationToken);
    }

    private static async Task<Dictionary<Guid, TrackStartTotal>> ReadExport(
        string path,
        CancellationToken cancellationToken
    )
    {
        await using var file = File.OpenRead(path);
        await using var decompressed = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
            ? new GZipStream(file, CompressionMode.Decompress)
            : null;
        using var reader = new StreamReader(decompressed ?? (Stream)file);
        var header = await reader.ReadLineAsync(cancellationToken);
        if (
            header
            is not ("uuid,plays,days_with_plays" or "source_track_uuid,plays,days_with_plays")
        )
        {
            throw new InvalidDataException(
                "Play export must contain track UUID, plays and days_with_plays."
            );
        }

        var totals = new Dictionary<Guid, TrackStartTotal>();
        var lineNumber = 1;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            lineNumber++;
            var fields = line.Split(',');
            if (
                fields.Length != 3
                || !Guid.TryParse(fields[0], out var id)
                || !long.TryParse(
                    fields[1],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var plays
                )
                || !long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out _)
                || !totals.TryAdd(id, new TrackStartTotal(id, plays))
            )
            {
                throw new InvalidDataException(
                    $"Invalid or duplicate play aggregate on line {lineNumber}."
                );
            }
        }

        return totals;
    }
}
