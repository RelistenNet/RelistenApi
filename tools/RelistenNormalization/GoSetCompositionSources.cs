using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization.Batch;

/// <summary>
/// Bounded first-party HTML acquisition. This writes original revisions and decoded
/// assertions only; correspondence to a catalog song belongs to a separately attributed
/// proposal. A failed prefix remains inspectable, without a completion manifest.
/// </summary>
public sealed class GoSetCompositionSources
{
    public const string Version = "go-set-composition-sources-v1";
    private const int MaximumPages = 8;
    private const int MaximumResponseBytes = 1024 * 1024;
    private const long MaximumOutputBytes = 32L * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient client;
    private readonly TimeProvider clock;

    public GoSetCompositionSources(HttpClient client, TimeProvider? clock = null)
    {
        this.client = client;
        this.clock = clock ?? TimeProvider.System;
    }

    public async Task<GoSetCompositionSourcesResult> RunAsync(
        string requestPath,
        string outputDirectory,
        string requestSha256,
        CancellationToken ct = default
    )
    {
        var requestBytes = await File.ReadAllBytesAsync(requestPath, ct);
        if (requestBytes.Length > MaximumResponseBytes || Hash(requestBytes) != requestSha256)
        {
            throw new InvalidDataException("The GoSet request differs from its frozen pin.");
        }
        var request =
            JsonSerializer.Deserialize<GoSetCompositionSourcesRequest>(requestBytes, Json)
            ?? throw new InvalidDataException("A GoSet acquisition request is required.");
        var pages = ValidateRequest(request);
        if (Directory.Exists(outputDirectory) || File.Exists(outputDirectory))
        {
            throw new ArgumentException("GoSet acquisition requires a new output directory.");
        }
        ct.ThrowIfCancellationRequested();
        CheckSpace(
            Path.GetDirectoryName(Path.GetFullPath(outputDirectory))!,
            request.MinimumFreeBytes
        );
        Directory.CreateDirectory(outputDirectory);
        var output = new Output(outputDirectory, request.MinimumFreeBytes);
        await output.WriteAsync("request.json", requestBytes, ct);
        var captures = new List<GoSetCompositionCapture>();

        // Each page has a fresh bounded deadline. There are no retries, pagination,
        // database admissions or model calls in this acquisition command.
        foreach (var page in pages)
        {
            ct.ThrowIfCancellationRequested();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            captures.Add(await CaptureAsync(page, captures.Count, output, deadline.Token));
        }

        var result = new GoSetCompositionSourcesResult(
            Version,
            "completed",
            requestSha256,
            NormalizationProviderDecoder.Version,
            captures
        );
        // Publication is last; consumers must not treat a retained prefix as complete.
        await output.WriteJsonAsync("manifest.json", result, ct);
        return result;
    }

    private async Task<GoSetCompositionCapture> CaptureAsync(
        Page page,
        int ordinal,
        Output output,
        CancellationToken ct
    )
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, page.Url);
        message.Headers.UserAgent.ParseAdd(
            "RelistenNormalization.EvidenceAudit/1.0 (https://relisten.net)"
        );
        message.Headers.Accept.ParseAdd("text/html");
        using var response = await client.SendAsync(
            message,
            HttpCompletionOption.ResponseHeadersRead,
            ct
        );
        var finalUrl = response.RequestMessage?.RequestUri ?? page.Url;
        var mediaType =
            response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
        var capturedAt = clock.GetUtcNow();
        var body = await ReadBodyAsync(response, ct);
        var prefix = ordinal.ToString("D3", CultureInfo.InvariantCulture);
        var bodyArtifact = await output.WriteAsync(prefix + ".html", body, ct);
        await output.WriteJsonAsync(
            prefix + ".receipt.json",
            new
            {
                requestedUrl = page.Url.AbsoluteUri,
                finalUrl = finalUrl.AbsoluteUri,
                statusCode = (int)response.StatusCode,
                mediaType,
                capturedAt,
                body = bodyArtifact,
            },
            ct
        );
        if (response.StatusCode != HttpStatusCode.OK || finalUrl != page.Url)
        {
            throw new InvalidDataException(
                "GoSet returned a non-success or redirected response; its bytes were retained."
            );
        }
        var input = new NormalizationInputRevision
        {
            Uuid = Guid.NewGuid(),
            Resource = page.Resource,
            Payload = body,
            Sha256 = bodyArtifact.Sha256,
            MediaType = mediaType,
            CaptureVersion = page.CaptureVersion,
            CapturedAt = capturedAt.UtcDateTime,
        };
        // Decode uses the shared media/charset/evidence parser and rejects an unsupported
        // representation. Preserve the original input even when parsing fails.
        var inputArtifact = await output.WriteJsonAsync(prefix + ".input.json", input, ct);
        var decoded = new NormalizationProviderDecoder().Decode(input, ct);
        var decodedArtifact = await output.WriteJsonAsync(prefix + ".decoded.json", decoded, ct);
        return new(
            page.Url.AbsoluteUri,
            finalUrl.AbsoluteUri,
            (int)response.StatusCode,
            mediaType,
            capturedAt,
            input.Uuid,
            page.Resource,
            bodyArtifact,
            inputArtifact,
            decodedArtifact
        );
    }

    private static Page[] ValidateRequest(GoSetCompositionSourcesRequest request)
    {
        if (
            request.Urls is null
            || request.Urls.Count is < 1 or > MaximumPages
            || request.MinimumFreeBytes <= 0
        )
        {
            throw new ArgumentException(
                "A GoSet request needs one to eight pages and a positive disk floor."
            );
        }
        var pages = request.Urls.Select(ParsePage).ToArray();
        if (
            pages.Select(page => page.Url.AbsoluteUri).Distinct(StringComparer.Ordinal).Count()
            != pages.Length
        )
        {
            throw new ArgumentException("GoSet page requests must be distinct.");
        }
        return pages;
    }

    private static Page ParsePage(string value)
    {
        if (
            !Uri.TryCreate(value, UriKind.Absolute, out var url)
            || url.Scheme != "https"
            || url.Host != "go-set.net"
            || !url.IsDefaultPort
            || url.UserInfo.Length != 0
            || url.Fragment.Length != 0
        )
        {
            throw new ArgumentException("Only canonical HTTPS go-set.net pages are supported.");
        }
        const string songPrefix = "/song/";
        if (
            url.AbsolutePath.StartsWith(songPrefix, StringComparison.Ordinal)
            && url.Query.Length == 0
        )
        {
            var slug = url.AbsolutePath[songPrefix.Length..];
            if (
                slug.Length > 0
                && slug.All(character => char.IsAsciiLetterOrDigit(character) || character == '-')
            )
            {
                return new(url, new("go-set.net", "song_page", slug), "go-set-song-html-v1");
            }
        }
        const string datePrefix = "?date=";
        if (
            url.AbsolutePath == "/setlists/"
            && url.Query.StartsWith(datePrefix, StringComparison.Ordinal)
            && DateOnly.TryParseExact(
                url.Query[datePrefix.Length..],
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var date
            )
        )
        {
            return new(
                url,
                new(
                    "go-set.net",
                    "setlist_page",
                    "date:" + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                ),
                "go-set-setlist-html-v1"
            );
        }
        throw new ArgumentException("Only a literal song page or exact setlist date is supported.");
    }

    private static async Task<byte[]> ReadBodyAsync(
        HttpResponseMessage response,
        CancellationToken ct
    )
    {
        if (response.Content.Headers.ContentLength > MaximumResponseBytes)
        {
            throw new InvalidDataException("GoSet response exceeds its one MiB body bound.");
        }
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var body = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int count;
        while ((count = await stream.ReadAsync(buffer, ct)) != 0)
        {
            if (body.Length + count > MaximumResponseBytes)
            {
                throw new InvalidDataException("GoSet response exceeds its one MiB body bound.");
            }
            body.Write(buffer, 0, count);
        }
        return body.ToArray();
    }

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void CheckSpace(string path, long minimumFreeBytes)
    {
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!);
        if (drive.AvailableFreeSpace < minimumFreeBytes)
        {
            throw new IOException("GoSet acquisition reached its free-space floor.");
        }
    }

    private sealed record Page(Uri Url, NormalizationResourceKey Resource, string CaptureVersion);

    private sealed class Output(string root, long minimumFreeBytes)
    {
        private long written;

        public Task<GoSetCompositionArtifact> WriteJsonAsync<T>(
            string name,
            T value,
            CancellationToken ct
        ) => WriteAsync(name, JsonSerializer.SerializeToUtf8Bytes(value, Json), ct);

        public async Task<GoSetCompositionArtifact> WriteAsync(
            string name,
            byte[] bytes,
            CancellationToken ct
        )
        {
            ct.ThrowIfCancellationRequested();
            if (written + bytes.LongLength > MaximumOutputBytes)
            {
                throw new IOException("GoSet acquisition reached its 32 MiB artifact bound.");
            }
            CheckSpace(root, checked(minimumFreeBytes + bytes.LongLength));
            var path = Path.Combine(root, name);
            await File.WriteAllBytesAsync(path + ".partial", bytes, ct);
            ct.ThrowIfCancellationRequested();
            File.Move(path + ".partial", path);
            written += bytes.LongLength;
            return new(name, bytes.LongLength, Hash(bytes));
        }
    }
}
