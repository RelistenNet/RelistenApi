using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationMusicBrainzInputService
{
    private async Task<MusicBrainzHttpResponse> FetchOnceAsync(
        MusicBrainzDispatch dispatch,
        MusicBrainzDispatchClock clock,
        CancellationToken ct
    )
    {
        var headers = (IReadOnlyDictionary<string, string[]>)new Dictionary<string, string[]>();
        int? status = null;
        string? finalUrl = null;
        if (clock.Elapsed >= dispatch.SendDeadline - dispatch.PreparedAt)
        {
            return Failure("send_deadline_elapsed");
        }

        var remaining = dispatch.HttpDeadline - dispatch.PreparedAt - clock.Elapsed;
        if (remaining <= TimeSpan.Zero)
        {
            return Failure("request_timeout");
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(remaining < policy.RequestTimeout ? remaining : policy.RequestTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, dispatch.Profile.RequestUrl);
            request.Headers.UserAgent.ParseAdd(dispatch.UserAgent);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.AcceptEncoding.ParseAdd("identity");
            using var response = await http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                deadline.Token
            );
            status = (int)response.StatusCode;
            finalUrl =
                response.RequestMessage?.RequestUri?.AbsoluteUri ?? dispatch.Profile.RequestUrl;
            headers = MusicBrainzHttpPolicy.SnapshotBackoffHeaders(
                response.Headers.Concat(response.Content.Headers)
            );
            try
            {
                headers = MusicBrainzHttpPolicy.SnapshotHeaders(
                    response.Headers.Concat(response.Content.Headers)
                );
            }
            catch (ArgumentException)
            {
                return Failure("response_metadata_too_large");
            }
            if (
                response.Content.Headers.ContentLength
                > MusicBrainzAcquisitionLimits.MaximumResponseBytes
            )
            {
                return Failure("response_too_large");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var body = new MemoryStream();
            var buffer = new byte[16 * 1024];
            while (true)
            {
                var read = await stream.ReadAsync(buffer, deadline.Token);
                if (read == 0)
                {
                    break;
                }
                if (body.Length + read > MusicBrainzAcquisitionLimits.MaximumResponseBytes)
                {
                    return Failure("response_too_large");
                }
                await body.WriteAsync(buffer.AsMemory(0, read), deadline.Token);
            }
            return BoundMetadata(
                new(
                    status,
                    finalUrl,
                    headers,
                    body.ToArray(),
                    true,
                    clock.UtcNow,
                    PreparationElapsed: clock.Elapsed
                )
            );
        }
        catch (OperationCanceledException)
        {
            return Failure(ct.IsCancellationRequested ? "caller_cancelled" : "request_timeout");
        }
        catch (Exception error) when (error is HttpRequestException or IOException)
        {
            return Failure("transport_failure");
        }

        MusicBrainzHttpResponse Failure(string reason) =>
            BoundMetadata(
                new(status, finalUrl, headers, null, false, clock.UtcNow, reason, clock.Elapsed)
            );

        static MusicBrainzHttpResponse BoundMetadata(MusicBrainzHttpResponse response) =>
            JsonSerializer.SerializeToUtf8Bytes(response with { Body = null }).Length
            <= MusicBrainzAcquisitionLimits.MaximumMetadataBytes
                ? response
                : response with
                {
                    Headers = MusicBrainzHttpPolicy.SnapshotBackoffHeaders(
                        response.Headers.Select(pair => new KeyValuePair<
                            string,
                            IEnumerable<string>
                        >(pair.Key, pair.Value))
                    ),
                    FinalUrl = null,
                    Body = null,
                    BodyComplete = false,
                    Reason = "response_metadata_too_large",
                };
    }

    private static string? ValidateRepresentation(
        MusicBrainzDispatch dispatch,
        MusicBrainzHttpResponse response
    )
    {
        if (!response.BodyComplete || response.Body == null)
        {
            return "incomplete_response";
        }
        if (response.FinalUrl != dispatch.Profile.RequestUrl)
        {
            return "response_origin_mismatch";
        }
        var encodings = response
            .Headers.Where(pair =>
                pair.Key.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase)
            )
            .SelectMany(pair => pair.Value)
            .SelectMany(value => value.Split(','));
        if (
            encodings.Any(value =>
                !value.Trim().Equals("identity", StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            return "unsupported_content_encoding";
        }
        if (
            !MediaTypeHeaderValue.TryParse(
                MusicBrainzHttpPolicy.Header(response, "Content-Type"),
                out var contentType
            )
            || !(
                contentType.MediaType!.Equals(
                    "application/json",
                    StringComparison.OrdinalIgnoreCase
                ) || contentType.MediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)
            )
            || (
                contentType.CharSet != null
                && !contentType
                    .CharSet.Trim('"')
                    .Equals("utf-8", StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            return "unsupported_media_type";
        }
        try
        {
            var bytes = response.Body.AsSpan();
            if (bytes.StartsWith(Encoding.UTF8.Preamble))
            {
                bytes = bytes[Encoding.UTF8.Preamble.Length..];
            }
            var text = new UTF8Encoding(false, true).GetString(bytes);
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("error", out _))
            {
                return dispatch.Profile.SearchDescriptor == null
                    ? "invalid_entity_envelope"
                    : "invalid_search_envelope";
            }
            if (dispatch.Profile.SearchDescriptor is { } search)
            {
                return ValidateSearchEnvelope(root, search);
            }
            if (
                !root.TryGetProperty("id", out var id)
                || id.ValueKind != JsonValueKind.String
                || !Guid.TryParseExact(id.GetString(), "D", out var uuid)
                || uuid.ToString("D") != dispatch.Profile.Resource.ProviderKey
            )
            {
                return "invalid_entity_envelope";
            }
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException)
        {
            return "invalid_json";
        }
        return null;
    }
}
