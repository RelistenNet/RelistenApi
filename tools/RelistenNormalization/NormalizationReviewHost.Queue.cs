using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization.Batch;

public sealed partial class NormalizationReviewHost
{
    private async Task<IResult> QueueAsync(HttpContext context)
    {
        var page = new ReviewQueuePage(
            QueueNumber(context.Request.Query["offset"].ToString(), 0, int.MaxValue),
            QueueNumber(context.Request.Query["limit"].ToString(), 10, 25),
            context.Request.Query["artist"].ToString(),
            context.Request.Query["category"].ToString(),
            context.Request.Query["domain"].ToString()
        );
        if (configuration.QueuePath == null)
        {
            return Results.Json(
                new
                {
                    total = 0,
                    offset = page.Offset,
                    limit = page.Limit,
                    artists = Array.Empty<object>(),
                    categories = Array.Empty<object>(),
                    items = Array.Empty<object>(),
                    limitations = new[] { "No retained benchmark queue is configured." },
                    queue = new { rows = 0, sha256 = "" },
                },
                Json
            );
        }

        var snapshot = await ReadQueueSnapshotAsync(
            configuration.QueuePath,
            page,
            context.RequestAborted
        );
        var cards = await BuildQueueCardsAsync(snapshot, context.RequestAborted);
        var result = new
        {
            total = snapshot.Total,
            offset = page.Offset,
            limit = page.Limit,
            artists = snapshot.Artists.Select(item => new
            {
                key = QueueText(item, "artistUuid"),
                label = QueueText(item, "artist"),
                count = snapshot.ArtistCounts.GetValueOrDefault(QueueText(item, "artistUuid")!),
                metrics = item,
            }),
            categories = snapshot
                .CategoryCounts.OrderBy(pair => pair.Key)
                .Select(pair => new
                {
                    key = pair.Key,
                    label = QueueCategoryLabel(pair.Key),
                    count = pair.Value,
                }),
            items = cards,
            limitations = snapshot.Limitations,
            queue = new { rows = snapshot.RowCount, sha256 = snapshot.Sha256 },
        };
        if (JsonSerializer.SerializeToUtf8Bytes(result, Json).Length > 1024 * 1024)
        {
            throw new ArgumentException("The queue page exceeds one MiB; request fewer rows.");
        }
        return Results.Json(result, Json);
    }

    /// <summary>Streams the authenticated benchmark, retaining counts and one bounded page.</summary>
    private static async Task<ReviewQueueSnapshot> ReadQueueSnapshotAsync(
        string path,
        ReviewQueuePage page,
        CancellationToken ct
    )
    {
        var before = new FileInfo(path);
        if (before.Name != "errors.jsonl" || !before.Exists || before.Length > 50 * 1024 * 1024)
        {
            throw new ArgumentException(
                "The configured queue must be a retained errors.jsonl of at most 50 MiB."
            );
        }
        var originalLength = before.Length;
        var originalModified = before.LastWriteTimeUtc;
        var manifestPath = Path.Combine(before.DirectoryName!, "manifest.json");
        if (new FileInfo(manifestPath).Length > 4 * 1024 * 1024)
        {
            throw new ArgumentException("The benchmark completion manifest exceeds four MiB.");
        }
        using var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(manifestPath, ct));
        var metadata = manifest.RootElement;
        if (
            QueueText(metadata, "version") != "artist-setlist-reference-benchmark-v1"
            || QueueText(metadata, "state") != "completed"
        )
        {
            throw new ArgumentException(
                "A completed artist-setlist benchmark is required for this queue."
            );
        }
        var declared = metadata
            .GetProperty("outputs")
            .EnumerateArray()
            .SingleOrDefault(item => Path.GetFullPath(QueueText(item, "path") ?? "") == path);
        await using var input = File.OpenRead(path);
        var sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, ct));
        if (
            declared.ValueKind != JsonValueKind.Object
            || declared.GetProperty("bytes").GetInt64() != originalLength
            || QueueText(declared, "sha256") != sha
        )
        {
            throw new NormalizationConflictException(
                "The benchmark queue differs from its completion manifest."
            );
        }
        var metrics = metadata
            .GetProperty("artists")
            .EnumerateArray()
            .Select(item => item.Clone())
            .ToArray();
        var artistCounts = new Dictionary<string, int>();
        var categoryCounts = new Dictionary<string, int>();
        var selected = new List<JsonElement>();
        var total = 0;
        var rows = 0;
        await foreach (var line in File.ReadLinesAsync(path, ct))
        {
            if (line.Length > 1024 * 1024)
            {
                throw new ArgumentException("A benchmark queue row exceeds one MiB.");
            }
            using var document = JsonDocument.Parse(line);
            var row = document.RootElement;
            var owner = QueueArtist(row);
            var status =
                QueueText(row, "status")
                ?? throw new ArgumentException("A benchmark row has no status.");
            rows++;
            if (page.Domain.Length != 0 && page.Domain != "songs")
            {
                continue;
            }
            artistCounts[owner] = artistCounts.GetValueOrDefault(owner) + 1;
            if (page.Artist.Length != 0 && owner != page.Artist)
            {
                continue;
            }
            categoryCounts[status] = categoryCounts.GetValueOrDefault(status) + 1;
            if (page.Category.Length != 0 && status != page.Category)
            {
                continue;
            }
            if (total >= page.Offset && selected.Count < page.Limit)
            {
                selected.Add(row.Clone());
            }
            total++;
        }
        var after = new FileInfo(path);
        if (after.Length != originalLength || after.LastWriteTimeUtc != originalModified)
        {
            throw new NormalizationConflictException("The benchmark queue changed during paging.");
        }
        return new(
            total,
            rows,
            sha,
            metrics,
            artistCounts,
            categoryCounts,
            selected.ToArray(),
            metadata
                .GetProperty("limitations")
                .EnumerateArray()
                .Select(item => item.GetString())
                .ToArray()
        );
    }

    /// <summary>Adds display wording and optional authenticated actions without changing the frozen benchmark.</summary>
    private async Task<object[]> BuildQueueCardsAsync(
        ReviewQueueSnapshot snapshot,
        CancellationToken ct
    )
    {
        var names = snapshot.Artists.ToDictionary(
            item => QueueText(item, "artistUuid")!,
            item => QueueText(item, "artist")!
        );
        var items = new List<object>();
        var actions = new Dictionary<ReviewReference, ReviewReaderItem?>();
        using var actionDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        actionDeadline.CancelAfter(TimeSpan.FromSeconds(2));
        var actionLookupAvailable = true;
        foreach (var row in snapshot.SelectedRows)
        {
            var owner = QueueArtist(row);
            var rawTitles = QueueArray(row, "filesForReview")
                .Select(item => QueueText(item, "title"))
                .Where(value => value != null)
                .Distinct()
                .Take(8)
                .ToArray();
            var missingTitles = QueueArray(row, "missingSongs")
                .Select(item => QueueText(item, "title"))
                .Where(value => value != null)
                .Take(8)
                .ToArray();
            ReviewReaderItem? action = null;
            if (
                Guid.TryParse(owner, out var artistUuid)
                && actionLookupAvailable
                && !actionDeadline.IsCancellationRequested
            )
            {
                try
                {
                    action = await TryQueueSourceAsync(
                        row,
                        artistUuid,
                        actions,
                        actionDeadline.Token
                    );
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    actionLookupAvailable = false;
                }
                catch (Exception error)
                    when (error is HttpRequestException or ArgumentException or JsonException)
                {
                    // Optional actions share one deadline. A failed bridge probe must
                    // leave the benchmark readable without serial reader timeouts.
                    actionLookupAvailable = false;
                }
            }
            var show = QueueText(row, "showUuid");
            var status = QueueText(row, "status")!;
            items.Add(
                new
                {
                    id = row.GetProperty("key").GetRawText(),
                    artist = names.GetValueOrDefault(owner, owner),
                    artistUuid = owner,
                    date = (string?)null,
                    category = status,
                    label = action?.DisplayName
                        ?? rawTitles.FirstOrDefault()
                        ?? QueueArray(row, "extraSongs")
                            .Select(song => QueueText(song, "title"))
                            .FirstOrDefault(title => !string.IsNullOrWhiteSpace(title))
                        ?? missingTitles.FirstOrDefault()
                        ?? "Unaligned setlist reference",
                    showUuid = show,
                    recordedTitles = rawTitles,
                    referenceTitles = missingTitles,
                    recordedTitlesLabel = "Raw file titles contributing extra predictions (sample)",
                    referenceTitlesLabel = "Reference memberships missing from predictions (sample)",
                    detail = status == "compared"
                        ? $"{QueueCount(row, "missing")} missing and {QueueCount(row, "extra")} extra song memberships. This comparison does not adjudicate individual files or compositions."
                        : $"Unscored: {status.Replace('_', ' ')}. Reference or prediction coverage needs review before song-presence comparison.",
                    sourceRef = action?.Reference,
                    actionLabel = action == null
                        ? null
                        : "Review song identity: " + action.DisplayName,
                }
            );
        }
        return items.ToArray();
    }

    private async Task<ReviewReaderItem?> TryQueueSourceAsync(
        JsonElement row,
        Guid artistUuid,
        Dictionary<ReviewReference, ReviewReaderItem?> cache,
        CancellationToken ct
    )
    {
        var keys = QueueArray(row, "extraSongs")
            .Select(song => QueueText(song, "entityKey"))
            .Concat(
                QueueArray(row, "filesForReview")
                    .SelectMany(file => QueueArray(file, "predicted"))
                    .Select(item => item.GetString())
            )
            .Concat(QueueArray(row, "missingSongs").Select(song => QueueText(song, "entityKey")))
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct()
            .Take(4);
        foreach (var key in keys)
        {
            ct.ThrowIfCancellationRequested();
            // This benchmark reads only groups.scope='catalogue'. Its captured entity key
            // supplies a lookup coordinate, and the reader must still authenticate that home.
            var reference = new ReviewReference(
                "songs",
                "repertoire",
                JsonSerializer.Serialize(new[] { "catalogue", key }, Json),
                artistUuid
            );
            if (!cache.TryGetValue(reference, out var source))
            {
                try
                {
                    source = await readers.SourceAsync(reference, ct);
                }
                catch (NormalizationConflictException)
                {
                    // A frozen prediction may no longer have a current reader account.
                    source = null;
                }
                cache.Add(reference, source);
            }
            if (source != null)
            {
                return source;
            }
        }
        return null;
    }

    private static string QueueCategoryLabel(string category) =>
        category switch
        {
            "compared" => "Song differences",
            "empty_reference" => "Empty reference",
            "missing_reference" => "Missing reference",
            "conflicting_reference" => "Conflicting references",
            "unaligned_reference" => "Unaligned reference",
            "unprojected_reference" => "Unprojected reference",
            "missing_predictions" => "Missing predictions",
            _ => category.Replace('_', ' '),
        };

    private static int QueueNumber(string value, int fallback, int maximum) =>
        value.Length == 0 ? fallback
        : int.TryParse(value, out var number) && number >= 0 && number <= maximum ? number
        : throw new ArgumentException("Invalid queue page bound.");

    private static string? QueueText(JsonElement value, string key) =>
        value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String
            ? item.GetString()
            : null;

    private static JsonElement[] QueueArray(JsonElement value, string key) =>
        value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.Array
            ? item.EnumerateArray().ToArray()
            : [];

    private static int QueueCount(JsonElement value, string key) =>
        value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.Array
            ? item.GetArrayLength()
            : 0;

    private static string QueueArtist(JsonElement row) =>
        QueueText(row, "artistUuid")
        ?? (
            row.TryGetProperty("reference", out var reference)
                ? QueueText(reference, "artistUuid")
                : null
        )
        ?? "unknown";

    private sealed record ReviewQueuePage(
        int Offset,
        int Limit,
        string Artist,
        string Category,
        string Domain
    );

    private sealed record ReviewQueueSnapshot(
        int Total,
        int RowCount,
        string Sha256,
        JsonElement[] Artists,
        IReadOnlyDictionary<string, int> ArtistCounts,
        IReadOnlyDictionary<string, int> CategoryCounts,
        JsonElement[] SelectedRows,
        string?[] Limitations
    );
}
