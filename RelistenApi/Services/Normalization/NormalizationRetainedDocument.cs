using System;
using System.Linq;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization;

/// <summary>
/// Reads retained provider bytes before extraction changes whitespace, HTML or entities.
/// JSON locators are JSON Pointers. HTML and plain text use the empty locator for the
/// whole decoded document; text spans always count Unicode code points in that value.
/// </summary>
public sealed class NormalizationRetainedDocument : IDisposable
{
    private readonly Guid revisionUuid;
    private readonly JsonDocument document;
    private readonly bool textOnly;
    private CachedText? cachedText;

    private NormalizationRetainedDocument(Guid revisionUuid, JsonDocument document, bool textOnly)
    {
        this.revisionUuid = revisionUuid;
        this.document = document;
        this.textOnly = textOnly;
    }

    /// <summary>
    /// Uses the retained HTTP charset, or detects a BOM when no charset was supplied.
    /// Unsupported representations and invalid JSON fail without including provider content.
    /// </summary>
    public static NormalizationRetainedDocument Open(
        NormalizationInputRevision input,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!MediaTypeHeaderValue.TryParse(input.MediaType, out var contentType))
        {
            throw new ArgumentException("Retained input has an invalid media type.");
        }

        var mediaType = contentType.MediaType!;
        var json =
            mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("text/json", StringComparison.OrdinalIgnoreCase)
            || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
        var textOnly =
            mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("text/plain", StringComparison.OrdinalIgnoreCase);
        if (!json && !textOnly)
        {
            throw new ArgumentException("Retained input has an unsupported media type.");
        }

        string text;
        try
        {
            // HTTP gives an explicit charset precedence over a BOM. Without a charset,
            // inspect longer BOMs first so UTF-32 LE is not mistaken for UTF-16 LE.
            var bytes = input.Payload.AsSpan();
            var encoding =
                contentType.CharSet is { } charset ? Encoding.GetEncoding(charset.Trim('"'))
                : bytes.StartsWith(Encoding.UTF8.Preamble) ? Encoding.UTF8
                : bytes.StartsWith(Encoding.UTF32.Preamble) ? Encoding.UTF32
                : bytes.StartsWith(Encoding.Unicode.Preamble) ? Encoding.Unicode
                : bytes.StartsWith(Encoding.BigEndianUnicode.Preamble) ? Encoding.BigEndianUnicode
                : bytes.StartsWith(new byte[] { 0, 0, 0xFE, 0xFF }) ? new UTF32Encoding(true, true)
                : Encoding.UTF8;
            var preamble = encoding.Preamble;
            var offset = bytes.StartsWith(preamble) ? preamble.Length : 0;
            var strict = Encoding.GetEncoding(
                encoding.CodePage,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback
            );
            text = strict.GetString(bytes[offset..]);
        }
        catch (ArgumentException)
        {
            throw new ArgumentException(
                "Retained input has an unsupported encoding or invalid encoded text."
            );
        }

        try
        {
            // Wrapping text in a JSON string gives every caller one value contract without
            // pretending that HTML is parsed JSON or assigning offsets to rendered text.
            var document = JsonDocument.Parse(textOnly ? JsonSerializer.Serialize(text) : text);
            return new(input.Uuid, document, textOnly);
        }
        catch (JsonException)
        {
            throw new ArgumentException("Retained input is not valid JSON.");
        }
    }

    /// <summary>Resolves an exact retained value. The result remains valid until disposal.</summary>
    public JsonElement Resolve(string pointer)
    {
        if (pointer.Length == 0)
        {
            return document.RootElement;
        }
        if (textOnly || !pointer.StartsWith('/'))
        {
            throw new ArgumentException("Evidence locator does not identify a retained value.");
        }

        var value = document.RootElement;
        foreach (var encoded in pointer[1..].Split('/'))
        {
            for (var escapeIndex = 0; escapeIndex < encoded.Length; escapeIndex++)
            {
                if (encoded[escapeIndex] != '~')
                {
                    continue;
                }
                if (++escapeIndex == encoded.Length || encoded[escapeIndex] is not ('0' or '1'))
                {
                    throw new ArgumentException(
                        "Evidence locator has an invalid JSON Pointer escape."
                    );
                }
            }

            var token = encoded.Replace("~1", "/").Replace("~0", "~");
            if (
                value.ValueKind == JsonValueKind.Object
                && value.TryGetProperty(token, out var child)
            )
            {
                value = child;
            }
            else if (
                value.ValueKind == JsonValueKind.Array
                && token.Length > 0
                && (token.Length == 1 || token[0] != '0')
                && token.All(character => character is >= '0' and <= '9')
                && int.TryParse(token, out var index)
                && index < value.GetArrayLength()
            )
            {
                value = value[index];
            }
            else
            {
                throw new ArgumentException("Evidence locator does not identify a retained value.");
            }
        }
        return value;
    }

    /// <summary>Checks the cited revision and reproduces an optional exact code-point quote.</summary>
    public void ValidateEvidence(NormalizedEvidenceReference evidence)
    {
        if (evidence.InputRevisionUuid != revisionUuid)
        {
            throw new ArgumentException("Evidence belongs to a different input revision.");
        }

        var value = Resolve(evidence.JsonPointer);
        if (
            evidence.StartCodePoint == null
            && evidence.EndCodePoint == null
            && evidence.Quote == null
        )
        {
            return;
        }
        if (
            value.ValueKind != JsonValueKind.String
            || evidence.StartCodePoint == null
            || evidence.EndCodePoint == null
            || evidence.Quote == null
        )
        {
            throw new ArgumentException(
                "Text evidence requires a string value, both offsets and an exact quote."
            );
        }

        // A source field can supply thousands of citations. Keep only its most recent
        // decoded scalar sequence, rather than allocating the whole field for every span.
        // Publish pointer and runes together so concurrent readers never mix two fields.
        var cached = Volatile.Read(ref cachedText);
        if (cached == null || cached.Pointer != evidence.JsonPointer)
        {
            cached = new(evidence.JsonPointer, value.GetString()!.EnumerateRunes().ToArray());
            Volatile.Write(ref cachedText, cached);
        }

        var runes = cached.Runes;
        var start = evidence.StartCodePoint.Value;
        var end = evidence.EndCodePoint.Value;
        if (
            start < 0
            || end < start
            || end > runes.Length
            || string.Concat(runes[start..end].Select(rune => rune.ToString())) != evidence.Quote
        )
        {
            throw new ArgumentException(
                "Evidence quote does not match its Unicode code-point span."
            );
        }
    }

    public void Dispose()
    {
        document.Dispose();
        Volatile.Write(ref cachedText, null);
    }

    private sealed record CachedText(string Pointer, Rune[] Runes);
}
