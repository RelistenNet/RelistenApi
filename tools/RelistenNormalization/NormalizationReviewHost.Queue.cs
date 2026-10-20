using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization.Batch;

public sealed partial class NormalizationReviewHost
{
    private async Task<IResult> QueueAsync(HttpContext context)
    {
        var page = new ReviewBenchmarkQueuePage(
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

        var snapshot = await new ReviewBenchmarkQueue().ReadAsync(
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
                metrics = item.TryGetProperty(snapshot.ComparisonBasis, out var comparison)
                    ? comparison
                    : item,
                benchmarkMetrics = item,
                comparisonBasis = snapshot.ComparisonBasis,
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
            limitations = new[]
            {
                snapshot.ComparisonBasis == "resolvedIdentity"
                    ? "Song differences compare resolved song identities. Original imported-ID metrics and source references remain attached as evidence."
                    : "Song differences compare the retained song identities; this legacy benchmark has no separate resolved comparison.",
            }.Concat(snapshot.Limitations),
            queue = new
            {
                rows = snapshot.RowCount,
                sha256 = snapshot.Sha256,
                version = snapshot.Version,
                comparisonBasis = snapshot.ComparisonBasis,
            },
        };
        if (JsonSerializer.SerializeToUtf8Bytes(result, Json).Length > 1024 * 1024)
        {
            throw new ArgumentException("The queue page exceeds one MiB; request fewer rows.");
        }
        return Results.Json(result, Json);
    }

    /// <summary>Adds display wording and optional authenticated actions without changing the frozen benchmark.</summary>
    private async Task<object[]> BuildQueueCardsAsync(
        ReviewBenchmarkQueueSnapshot snapshot,
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
        foreach (var entry in snapshot.SelectedRows)
        {
            var row = entry.Original;
            var comparison = entry.Comparison;
            var owner = entry.ArtistUuid;
            var rawTitles = entry.RecordedTitles;
            var missingTitles = QueueArray(comparison, "missingSongs")
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
                        entry,
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
            var status = entry.Status;
            var comparisonLabel = entry.UsesResolvedIdentity
                ? "resolved song identities"
                : "retained song IDs";
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
                        ?? QueueArray(comparison, "extraSongs")
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
                        ? $"{QueueCount(comparison, "missing")} missing and {QueueCount(comparison, "extra")} extra song memberships using {comparisonLabel}. This comparison does not adjudicate individual files or compositions."
                        : $"Unscored: {status.Replace('_', ' ')}. Reference or prediction coverage needs review before song-presence comparison.",
                    comparisonBasis = snapshot.ComparisonBasis,
                    comparisonCounts = new
                    {
                        expected = QueueCount(comparison, "expected"),
                        predicted = QueueCount(comparison, "predicted"),
                        missing = QueueCount(comparison, "missing"),
                        extra = QueueCount(comparison, "extra"),
                    },
                    rawIdentityCounts = row.TryGetProperty("rawIdentity", out var raw)
                        ? new
                        {
                            expected = QueueCount(raw, "expected"),
                            predicted = QueueCount(raw, "predicted"),
                            missing = QueueCount(raw, "missing"),
                            extra = QueueCount(raw, "extra"),
                        }
                        : null,
                    referenceAccounts = QueueArray(row, "referenceAccounts"),
                    reference = row.TryGetProperty("reference", out var reference)
                        ? (JsonElement?)reference
                        : null,
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
        ReviewBenchmarkQueueRow entry,
        Guid artistUuid,
        Dictionary<ReviewReference, ReviewReaderItem?> cache,
        CancellationToken ct
    )
    {
        var keys = entry.ActionKeys.Take(4);
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
}
