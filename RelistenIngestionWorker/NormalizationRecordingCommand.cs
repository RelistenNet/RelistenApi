using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Relisten.Data;

namespace RelistenIngestionWorker;

/// <summary>The complete immutable selection requested by one explicit operator command.</summary>
public sealed record NormalizationRecordingCommandRequest(
    Guid PageUuid,
    Guid? ArtistUuid,
    Guid? AfterSourceUuid,
    int Limit
);

/// <summary>
/// Translates one recording page command into typed processing and safe JSON receipts.
/// Parsing runs before resolving catalog services; execution does not resolve Temporal clients
/// or start hosted workers. Exit code 2 means unfinished retryable work remains on this page.
/// </summary>
public sealed class NormalizationRecordingCommand(NormalizationRecordingPageService pages)
{
    private static readonly JsonSerializerOptions OutputOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    /// <summary>Validates explicit UUID selectors and the bounded item limit without opening a database.</summary>
    public static NormalizationRecordingCommandRequest Parse(IConfiguration configuration)
    {
        var page = ParseUuid(configuration["normalize-recordings"], "Page", required: true)!.Value;
        var artist = ParseUuid(configuration["artist"], "Artist");
        var after = ParseUuid(configuration["after"], "After-source");
        var limit = NormalizationRecordingPageService.DefaultLimit;
        if (
            configuration["limit"] is { } text
            && (!int.TryParse(text, out limit) || limit is < 1 or > 100)
        )
        {
            throw new ArgumentException(
                "Recording page limit must be an integer from 1 through 100."
            );
        }

        return new(page, artist, after, limit);
    }

    /// <summary>
    /// Writes one JSON receipt without raw evidence or exception details. A settled page returns
    /// zero even when attention outcomes remain; its published/missing/unresolved/stale counts
    /// distinguish those outcomes. An unfinished page returns 2 and supplies no continuation.
    /// </summary>
    public async Task<int> RunAsync(
        NormalizationRecordingCommandRequest request,
        TextWriter output,
        CancellationToken cancellationToken = default
    )
    {
        var result = await pages.ProcessPageAsync(
            request.PageUuid,
            request.ArtistUuid,
            request.AfterSourceUuid,
            request.Limit,
            cancellationToken
        );
        await output.WriteLineAsync(
            JsonSerializer.Serialize(result, OutputOptions).AsMemory(),
            cancellationToken
        );
        return result.Settled ? 0 : 2;
    }

    private static Guid? ParseUuid(string? value, string label, bool required = false)
    {
        if (value == null && !required)
        {
            return null;
        }
        if (!Guid.TryParse(value, out var uuid) || uuid == Guid.Empty)
        {
            throw new ArgumentException($"{label} selector must be a nonempty UUID.");
        }
        return uuid;
    }
}
