using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization;

/// <summary>One Responses request, without SDK retries or upstream error-body logging.</summary>
internal sealed class LunaRecordingCreditClient(HttpClient http, TimeSpan timeout)
{
    internal async Task<LunaCreditResponse> ExtractAsync(
        string renderedRequest,
        CancellationToken cancellationToken,
        string apiKey
    )
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://api.openai.com/v1/responses"
        );
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(renderedRequest, Encoding.UTF8, "application/json");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        using var response = await http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            deadline.Token
        );
        var requestId = response.Headers.TryGetValues("x-request-id", out var values)
            ? values.FirstOrDefault()
            : null;
        if (!response.IsSuccessStatusCode)
        {
            throw new LunaCreditResponseException("provider_error", providerRequestId: requestId);
        }
        // The nullable casts below distinguish an unfinished read from a complete empty body.
        byte[]? body = null;
        try
        {
            body = await ReadBodyAsync(response.Content, deadline.Token);
            return ParseResponse(body, requestId);
        }
        catch (System.IO.IOException)
        {
            throw new LunaCreditResponseException("provider_error", providerRequestId: requestId);
        }
        catch (OperationCanceledException)
        {
            throw new LunaCreditResponseException(
                cancellationToken.IsCancellationRequested ? "cancelled" : "timeout",
                providerRequestId: requestId
            );
        }
        catch (JsonException)
        {
            throw new LunaCreditResponseException(
                "invalid_response",
                providerRequestId: requestId,
                failureStage: LunaCreditFailureStage.ResponseEnvelope,
                responseBody: body is null
                    ? (ReadOnlyMemory<byte>?)null
                    : new ReadOnlyMemory<byte>(body)
            );
        }
        catch (LunaCreditResponseException error)
        {
            throw new LunaCreditResponseException(
                error.Reason,
                error.Receipt,
                error.ProviderRequestId ?? requestId,
                error.FailureStage
                    ?? (
                        error.Reason == "invalid_response"
                            ? LunaCreditFailureStage.ResponseEnvelope
                            : null
                    ),
                body is null ? (ReadOnlyMemory<byte>?)null : new ReadOnlyMemory<byte>(body)
            );
        }
    }

    private static async Task<byte[]> ReadBodyAsync(
        HttpContent content,
        CancellationToken cancellationToken
    )
    {
        // Bound provider bytes independently of content-length and of the generation cap.
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new System.IO.MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk, cancellationToken)) != 0)
        {
            if (buffer.Length + count > LunaCreditFailureDiagnostic.MaxResponseBodyBytes)
            {
                throw new LunaCreditResponseException("invalid_response");
            }
            buffer.Write(chunk, 0, count);
        }
        return buffer.ToArray();
    }

    internal static LunaCreditResponse ParseResponse(byte[] body, string? requestId)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (
            root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("usage", out var usage)
            || usage.ValueKind != JsonValueKind.Object
            || !usage.TryGetProperty("input_tokens", out var input)
            || input.ValueKind != JsonValueKind.Number
            || !input.TryGetInt64(out var inputTokens)
            || !usage.TryGetProperty("output_tokens", out var result)
            || result.ValueKind != JsonValueKind.Number
            || !result.TryGetInt64(out var outputTokens)
            || inputTokens < 0
            || outputTokens < 0
        )
        {
            throw new LunaCreditResponseException("invalid_response", providerRequestId: requestId);
        }
        // Usage remains a receipt even when refusal, truncation or bad structure prevents proposals.
        var receipt = new LunaCreditResponse("", requestId, inputTokens, outputTokens)
        {
            Envelope = body,
        };
        if (
            !root.TryGetProperty("status", out var status)
            || status.ValueKind != JsonValueKind.String
            || status.GetString() != "completed"
            || !root.TryGetProperty("output", out var output)
            || output.ValueKind != JsonValueKind.Array
        )
        {
            throw new LunaCreditResponseException("invalid_response", receipt);
        }
        var texts = new System.Collections.Generic.List<string>();
        foreach (var item in output.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new LunaCreditResponseException("invalid_response", receipt);
            }
            if (
                item.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String
                && type.GetString() == "message"
            )
            {
                if (
                    !item.TryGetProperty("content", out var content)
                    || content.ValueKind != JsonValueKind.Array
                )
                {
                    throw new LunaCreditResponseException("invalid_response", receipt);
                }
                foreach (var part in content.EnumerateArray())
                {
                    if (
                        part.ValueKind != JsonValueKind.Object
                        || !part.TryGetProperty("type", out var partType)
                        || partType.ValueKind != JsonValueKind.String
                        || partType.GetString() != "output_text"
                        || !part.TryGetProperty("text", out var text)
                        || text.ValueKind != JsonValueKind.String
                    )
                    {
                        throw new LunaCreditResponseException("invalid_response", receipt);
                    }
                    texts.Add(text.GetString()!);
                }
            }
        }
        if (texts.Count != 1)
        {
            throw new LunaCreditResponseException("invalid_response", receipt);
        }
        return receipt with { Json = texts[0] };
    }
}
