using System.Text.Json;

namespace Relisten.Normalization.Batch;

public sealed partial class ShadowPerformanceAdapter
{
    private void ObserveSource(JsonElement row)
    {
        var source = SourceHome.Read(row);
        if (sources.TryGetValue(source.Id, out var existing) && existing != source)
        {
            conflictingSources.Add(source.Id);
        }
        sources.TryAdd(source.Id, source);
    }

    private ShadowPerformanceHome CatalogHome(ShadowCatalogInput input)
    {
        var isFile = input.Kind == "track_sample";
        var sourceId = isFile ? Number(input.Row, "source_id") : Number(input.Row, "id");
        var problems = new List<string>();
        var source = ReadSourceHome(input, sourceId, problems);
        var show = ReadShowHome(input, source, problems);
        if (source == null)
        {
            problems.Add("missing_source_context");
        }
        else
        {
            if (conflictingSources.Contains(source.Id))
            {
                problems.Add("conflicting_source_context");
            }
            if (source.ArtistId != input.Artist.Id)
            {
                problems.Add("source_artist_context_conflict");
            }
        }
        if (show == null)
        {
            problems.Add("missing_show_context");
        }
        else if (conflictingShows.Contains(show.Id) || show.ArtistId != source?.ArtistId)
        {
            problems.Add("conflicting_show_context");
        }
        var set = isFile ? Object(input.Record, "set_context") : null;
        if (set is { } candidate && Number(candidate, "source_id") != sourceId)
        {
            problems.Add("source_set_context_mismatch");
            set = null;
        }
        return new(
            show?.Uuid,
            source?.ShowId,
            source?.Uuid,
            isFile ? input.Uuid : null,
            set is { } setRow ? Uuid(setRow, "uuid") : null,
            source?.LiteralDate ?? show?.LiteralDate,
            show?.PerformanceType ?? "unknown",
            show?.PerformanceLabel,
            problems
        );
    }

    private SourceHome? ReadSourceHome(
        ShadowCatalogInput input,
        long? sourceId,
        List<string> problems
    )
    {
        if (input.Kind == "source_sample")
        {
            return SourceHome.Read(input.Row);
        }
        if (Object(input.Record, "source_context") is { } local)
        {
            var source = SourceHome.Read(local);
            if (source.Id == sourceId)
            {
                return source;
            }
            problems.Add("file_source_context_mismatch");
        }
        // A shared index cannot choose one UUID from conflicting captured rows.
        // File-local coordinates remain usable as the file's retained catalog home.
        return sourceId is { } id && !conflictingSources.Contains(id)
            ? sources.GetValueOrDefault(id)
            : null;
    }

    private ShowHome? ReadShowHome(
        ShadowCatalogInput input,
        SourceHome? source,
        List<string> problems
    )
    {
        if (Object(input.Record, "show_context") is { } local)
        {
            var show = ShowHome.Read(local);
            if (show.Id == source?.ShowId)
            {
                return show;
            }
            problems.Add(
                input.Kind == "track_sample"
                    ? "file_show_context_mismatch"
                    : "source_show_context_mismatch"
            );
        }
        return source?.ShowId is { } id && !conflictingShows.Contains(id)
            ? shows.GetValueOrDefault(id)
            : null;
    }

    private static string[] HomeKeys(ShadowPerformanceHome home) =>
        new[]
        {
            home.ShowUuid is { } show ? "show:" + show.ToString("D") : null,
            home.SourceUuid is { } source ? "source:" + source.ToString("D") : null,
            home.FileUuid is { } file ? "file:" + file.ToString("D") : null,
            home.SourceSetUuid is { } set ? "source_set:" + set.ToString("D") : null,
        }
            .OfType<string>()
            .ToArray();

    private static JsonElement? Object(JsonElement value, string name) =>
        value.TryGetProperty(name, out var found) && found.ValueKind == JsonValueKind.Object
            ? found
            : null;

    private static string? Text(JsonElement value, string name) =>
        value.TryGetProperty(name, out var found) && found.ValueKind == JsonValueKind.String
            ? found.GetString()
            : null;

    private static long? Number(JsonElement value, string name) =>
        value.TryGetProperty(name, out var found)
        && found.ValueKind == JsonValueKind.Number
        && found.TryGetInt64(out var number)
            ? number
            : null;

    private static Guid? Uuid(JsonElement value, string name) =>
        value.TryGetProperty(name, out var found)
        && found.ValueKind == JsonValueKind.String
        && found.TryGetGuid(out var uuid)
            ? uuid
            : null;

    private sealed record SourceHome(
        long Id,
        Guid Uuid,
        long ArtistId,
        long? ShowId,
        string? LiteralDate
    )
    {
        internal static SourceHome Read(JsonElement row) =>
            new(
                row.GetProperty("id").GetInt64(),
                row.GetProperty("uuid").GetGuid(),
                row.GetProperty("artist_id").GetInt64(),
                Number(row, "show_id"),
                Text(row, "display_date")
            );
    }

    private sealed record ShowHome(
        long Id,
        Guid Uuid,
        long ArtistId,
        string? LiteralDate,
        string PerformanceType,
        string? PerformanceLabel
    )
    {
        internal static ShowHome Read(JsonElement row) =>
            new(
                row.GetProperty("id").GetInt64(),
                row.GetProperty("uuid").GetGuid(),
                row.GetProperty("artist_id").GetInt64(),
                Text(row, "display_date") ?? Text(row, "date"),
                Text(row, "performance_type") ?? "unknown",
                Text(row, "performance_label")
            );
    }
}
