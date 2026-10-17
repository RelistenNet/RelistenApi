using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Relisten.Api.Models;

namespace Relisten.Data;

public sealed partial class CollectionService
{
    // Tokens bind the seek key to its collection and resource. They carry no authority:
    // every read still checks collection publication and joins current playable catalog rows.
    private sealed record Cursor(
        int Version,
        string Kind,
        Guid Collection,
        Guid Uuid,
        string Sort,
        Guid? Artist
    );

    private static Cursor? ReadCursor(
        string? token,
        string kind,
        Guid collection,
        int limit,
        Guid? artist = null
    )
    {
        if (limit < 1 || limit > 200)
        {
            throw new ArgumentException("Limit must be between 1 and 200.", nameof(limit));
        }
        if (token == null)
        {
            return null;
        }
        try
        {
            if (token.Length == 0 || token.Length > 4096)
            {
                throw new FormatException();
            }
            var normalized = token.Replace('-', '+').Replace('_', '/');
            normalized = normalized.PadRight((normalized.Length + 3) / 4 * 4, '=');
            var cursor = JsonSerializer.Deserialize<Cursor>(Convert.FromBase64String(normalized));
            if (
                cursor == null
                || cursor.Version != 1
                || cursor.Kind != kind
                || cursor.Collection != collection
                || cursor.Artist != artist
                || cursor.Uuid == Guid.Empty
                || cursor.Sort == null
                || (
                    kind == "recordings"
                    && !DateOnly.TryParseExact(
                        cursor.Sort,
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out _
                    )
                )
            )
            {
                throw new FormatException();
            }
            return cursor;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw new ArgumentException(
                "The cursor is invalid for this collection resource.",
                nameof(token),
                exception
            );
        }
    }

    private static string NewCursor(
        string kind,
        Guid collection,
        Guid uuid,
        string sort,
        Guid? artist = null
    ) =>
        Convert
            .ToBase64String(
                JsonSerializer.SerializeToUtf8Bytes(
                    new Cursor(1, kind, collection, uuid, sort, artist)
                )
            )
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static CollectionPage<T> Page<T>(T[] rows, int limit, Func<T, string> cursor) =>
        new()
        {
            items = rows.Take(limit).ToArray(),
            next_cursor = rows.Length > limit ? cursor(rows[limit - 1]) : null,
        };
}
