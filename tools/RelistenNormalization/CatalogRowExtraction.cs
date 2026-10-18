using System.Text.Json;
using System.Text.Json.Serialization;

namespace Relisten.Normalization.Batch;

/// <summary>
/// Runs the existing local extractors for one retained source or file row. The
/// caller supplies the artist UUID from its captured artist index and owns snapshot
/// validation, duplicate detection and error accounting. This class keeps no rows,
/// opens no files and performs no provider, database or model requests.
/// </summary>
public sealed class CatalogRowExtraction(bool includeSourceActorAliases = true)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    private static readonly string[] CreditFields =
    [
        "taper",
        "transferrer",
        "description",
        "source",
        "lineage",
        "taper_notes",
    ];

    private readonly RecordingCreditExtractor credits = new();
    private readonly SourceActorAliasExtractor sourceActors = new();
    private readonly TrackTitleExtractor titles = new();

    /// <summary>
    /// Projects source_sample or track_sample without changing producer output,
    /// evidence spans or identifiers. artistUuid must identify row.artist_id.
    /// Unsupported kinds and malformed row values fail before any result is returned;
    /// callers may retain that input as an error and continue with the next row.
    /// </summary>
    public NormalizationBatchExtraction Extract(JsonElement record, Guid artistUuid)
    {
        var kind = record.GetProperty("kind").GetString();
        if (kind is not ("source_sample" or "track_sample"))
        {
            throw new NotSupportedException(
                "Catalog row extraction supports source_sample and track_sample."
            );
        }

        var row = record.GetProperty("row");
        var artistId = row.GetProperty("artist_id").GetInt64();
        var resourceUuid = row.GetProperty("uuid").GetGuid();
        var family = record.TryGetProperty("family", out var value) ? value.GetString() : null;
        var inputs =
            kind == "source_sample"
                ? CreditFields.ToDictionary(field => field, field => Text(row, field))
                : new Dictionary<string, string?> { ["title"] = Text(row, "title") };
        var creditExtraction = kind == "source_sample" ? credits.Extract(inputs) : null;
        var titleExtraction =
            kind == "track_sample" ? titles.Extract(artistUuid, inputs["title"] ?? "") : null;
        var context = kind == "track_sample" ? TrackContext(record) : null;

        return new(
            kind,
            row.GetProperty("id").GetInt64(),
            resourceUuid,
            artistId,
            artistUuid,
            family,
            inputs,
            context,
            creditExtraction,
            titleExtraction,
            includeSourceActorAliases && kind == "source_sample"
                ? sourceActors.Extract(inputs)
                : null
        );
    }

    /// <summary>
    /// Returns an independently owned JSON value in the existing extraction wire
    /// format. A streaming shadow runner can consume it without a second JSONL file
    /// or retaining the input document after processing this row.
    /// </summary>
    public JsonElement ExtractJson(JsonElement record, Guid artistUuid) =>
        JsonSerializer.SerializeToElement(Extract(record, artistUuid), Json);

    private static NormalizationTrackInputContext TrackContext(JsonElement record) =>
        new(
            ContextUuid(record, "source_context"),
            ContextUuid(record, "set_context"),
            ContextUuid(record, "show_context")
        );

    private static string? Text(JsonElement row, string field) =>
        row.TryGetProperty(field, out var value) ? value.GetString() : null;

    private static Guid? ContextUuid(JsonElement record, string name) =>
        record.TryGetProperty(name, out var context) && context.ValueKind != JsonValueKind.Null
            ? context.GetProperty("uuid").GetGuid()
            : null;
}
