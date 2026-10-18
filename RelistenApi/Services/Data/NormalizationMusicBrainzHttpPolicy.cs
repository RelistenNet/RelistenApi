using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

/// <summary>Fixed request representations and bounded receipt fields shared by transport and storage.</summary>
internal static class MusicBrainzHttpPolicy
{
    private static readonly HashSet<string> ReceiptHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Type",
        "Content-Encoding",
        "Content-Length",
        "Date",
        "ETag",
        "Last-Modified",
        "Retry-After",
        "X-RateLimit-Limit",
        "X-RateLimit-Remaining",
        "X-RateLimit-Reset",
        "X-RateLimit-Zone",
        "X-RateLimit-Who",
        "X-MB-Rate-Limiter",
    };

    internal static MusicBrainzRequestProfile Profile(MusicBrainzLookupRequest request)
    {
        if (
            request.Mbid == Guid.Empty
            || !Enum.IsDefined(request.Kind)
            || !Enum.IsDefined(request.CacheMode)
            || (
                request.CacheMode == MusicBrainzCacheMode.ReuseFresh
                && request.MaximumCacheAge is not { Ticks: > 0 }
            )
            || (
                request.CacheMode == MusicBrainzCacheMode.Refresh && request.MaximumCacheAge != null
            )
        )
        {
            throw new ArgumentException(
                "A supported entity MBID and explicit valid cache policy are required.",
                nameof(request)
            );
        }
        var (kind, version, includes) = request.Kind switch
        {
            MusicBrainzLookupKind.Work => (
                "work",
                "musicbrainz-work-json-v1",
                "artist-rels aliases"
            ),
            MusicBrainzLookupKind.Recording => (
                "recording",
                "musicbrainz-recording-work-json-v1",
                "work-rels artist-credits"
            ),
            MusicBrainzLookupKind.Artist => ("artist", "musicbrainz-artist-json-v1", "aliases"),
            _ => throw new ArgumentException("Unsupported MusicBrainz lookup kind."),
        };
        var key = request.Mbid.ToString("D");
        return new(
            new("musicbrainz.org", kind, key),
            version,
            "https://musicbrainz.org/ws/2/"
                + kind
                + "/"
                + key
                + "?inc="
                + Uri.EscapeDataString(includes)
                + "&fmt=json"
        );
    }

    internal static MusicBrainzAcquisitionOptions SnapshotPolicy(
        MusicBrainzAcquisitionOptions options
    )
    {
        if (
            options.RequestTimeout <= TimeSpan.Zero
            || options.RequestTimeout > TimeSpan.FromSeconds(30)
            || options.SendWindow <= TimeSpan.Zero
            || options.SendWindow > TimeSpan.FromSeconds(5)
            || options.LeaseDuration
                <= options.SendWindow + options.RequestTimeout + TimeSpan.FromSeconds(5)
            || options.LeaseDuration > TimeSpan.FromSeconds(60)
        )
        {
            throw new ArgumentException(
                "Use bounded deadlines and a lease longer than the send, request and cleanup windows.",
                nameof(options)
            );
        }
        using var request = new HttpRequestMessage();
        if (
            string.IsNullOrWhiteSpace(options.UserAgent)
            || !request.Headers.UserAgent.TryParseAdd(options.UserAgent)
            || !options.UserAgent.Contains('/')
            || !(
                options.UserAgent.Contains("http", StringComparison.OrdinalIgnoreCase)
                || options.UserAgent.Contains('@')
            )
        )
        {
            throw new ArgumentException(
                "MusicBrainz requires an application/version User-Agent with a contact URL or email.",
                nameof(options)
            );
        }
        return new()
        {
            UserAgent = options.UserAgent,
            RequestTimeout = options.RequestTimeout,
            SendWindow = options.SendWindow,
            LeaseDuration = options.LeaseDuration,
        };
    }

    internal static IReadOnlyDictionary<string, string[]> SnapshotHeaders(
        IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers
    )
    {
        var result = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in headers)
        {
            if (ReceiptHeaders.Contains(pair.Key))
            {
                result[pair.Key] = pair.Value.ToArray();
            }
        }
        if (
            JsonSerializer.SerializeToUtf8Bytes(result).Length
            > MusicBrainzAcquisitionLimits.MaximumMetadataBytes
        )
        {
            throw new ArgumentException("MusicBrainz receipt metadata exceeds its bound.");
        }
        return result;
    }

    internal static MusicBrainzHttpResponse SnapshotResponse(MusicBrainzHttpResponse response)
    {
        if (
            response.BodyComplete != (response.Body != null)
            || response.Body?.Length > MusicBrainzAcquisitionLimits.MaximumResponseBytes
        )
        {
            throw new ArgumentException(
                "A complete bounded response body or an explicit missing body is required.",
                nameof(response)
            );
        }
        var headers = SnapshotHeaders(
            response.Headers.Select(pair => new KeyValuePair<string, IEnumerable<string>>(
                pair.Key,
                pair.Value
            ))
        );
        var frozen = response with { Headers = headers, Body = response.Body?.ToArray() };
        if (
            JsonSerializer.SerializeToUtf8Bytes(frozen with { Body = null }).Length
            > MusicBrainzAcquisitionLimits.MaximumMetadataBytes
        )
        {
            throw new ArgumentException(
                "MusicBrainz receipt metadata exceeds its bound.",
                nameof(response)
            );
        }
        return frozen;
    }

    internal static string? Header(MusicBrainzHttpResponse response, string name) =>
        response
            .Headers.FirstOrDefault(pair =>
                string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)
            )
            .Value?.FirstOrDefault();

    internal static IReadOnlyDictionary<string, string[]> SnapshotBackoffHeaders(
        IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers
    )
    {
        var now = DateTime.UtcNow;
        DateTime? latest = null;
        string? selected = null;
        foreach (
            var value in headers
                .Where(pair => pair.Key.Equals("Retry-After", StringComparison.OrdinalIgnoreCase))
                .SelectMany(pair => pair.Value)
        )
        {
            // RFC date/delta forms fit comfortably within this bound. Do not copy a hostile
            // oversized header merely because ordinary receipt metadata was already rejected.
            if (
                value.Length <= 256
                && RetryAt(value, now) is { } retryAt
                && (latest == null || retryAt > latest)
            )
            {
                latest = retryAt;
                selected = value;
            }
        }
        return selected == null
            ? new Dictionary<string, string[]>()
            : new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["Retry-After"] = [selected],
            };
    }

    internal static DateTime ResponseCooldown(MusicBrainzHttpResponse response, DateTime now)
    {
        var completed = response.CompletedAt.UtcDateTime;
        var notBefore =
            (completed > now ? completed : now) + MusicBrainzAcquisitionLimits.MinimumGap;
        foreach (
            var value in response
                .Headers.Where(pair =>
                    pair.Key.Equals("Retry-After", StringComparison.OrdinalIgnoreCase)
                )
                .SelectMany(pair => pair.Value)
        )
        {
            if (RetryAt(value, now) is { } retryAt && retryAt > notBefore)
            {
                notBefore = retryAt;
            }
        }
        return notBefore;
    }

    private static DateTime? RetryAt(string value, DateTime now)
    {
        // Npgsql maps DateTime.MaxValue to PostgreSQL infinity. Keep extreme provider
        // backoff finite so the gate's absent-time handling cannot turn it into no cooldown.
        var maximum = new DateTime(9998, 12, 31, 23, 59, 59, DateTimeKind.Utc);
        if (
            long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            && seconds >= 0
        )
        {
            var maximumSeconds = (maximum - now).TotalSeconds;
            return seconds >= maximumSeconds ? maximum : now.AddSeconds(seconds);
        }
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var date
        )
            ? date.UtcDateTime > maximum
                ? maximum
                : date.UtcDateTime
            : null;
    }
}
