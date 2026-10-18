using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Normalization;

namespace Relisten.Normalization.Batch;

/// <summary>Drives one durable acquisition step and exports its retained evidence into a new directory.</summary>
public sealed class MusicBrainzAcquisitionBatch(
    NormalizationMusicBrainzInputService service,
    NormalizationMusicBrainzInputStore store,
    NormalizationEvidenceService evidence
)
{
    public const string Version = "musicbrainz-acquisition-batch-v1";
    private const int MaximumRequestBytes = 64 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false) },
    };

    /// <summary>
    /// Validates the request before acquisition, then drives at most one GET. Pending is a completed
    /// local invocation, not a completed acquisition. Resume with the same command and request in a
    /// new directory. Cancellation or unexpected failure leaves no manifest.json; durable response
    /// recovery does not require another GET when the acquisition service already saved it.
    /// </summary>
    public async Task<MusicBrainzAcquisitionBatchReport> RunAsync(
        string requestPath,
        string outputDirectory,
        Guid commandUuid,
        CancellationToken ct = default
    )
    {
        ct.ThrowIfCancellationRequested();
        if (commandUuid == Guid.Empty)
        {
            throw new ArgumentException("Use a nonempty acquisition command UUID.");
        }
        var requestBytes = await ReadRequestBytes(requestPath, ct);
        var request = ParseRequest(requestBytes);
        outputDirectory = ClaimOutputDirectory(outputDirectory);

        try
        {
            var artifacts = new Dictionary<string, MusicBrainzAcquisitionArtifact>();
            await WriteBytes(outputDirectory, "request.json", requestBytes, artifacts, ct);
            var acquisition = await service.AcquireAsync(commandUuid, request, ct);
            await ExportAcquisition(outputDirectory, acquisition, artifacts, ct);
            var report = new MusicBrainzAcquisitionBatchReport(
                acquisition.State.ToString().ToLowerInvariant(),
                commandUuid,
                acquisition.NotBefore,
                outputDirectory
            );
            var manifest = new
            {
                schemaVersion = 1,
                report,
                versions = new
                {
                    batch = Version,
                    acquisition = acquisition.AcquisitionVersion,
                    decoder = NormalizationProviderDecoder.Version,
                    toolAssembly = await AssemblyReceipt(
                        typeof(MusicBrainzAcquisitionBatch).Assembly,
                        ct
                    ),
                    apiAssembly = await AssemblyReceipt(
                        typeof(NormalizationMusicBrainzInputService).Assembly,
                        ct
                    ),
                },
                artifacts,
            };
            await WriteJson(
                outputDirectory,
                "manifest.partial.json",
                manifest,
                artifacts: null,
                ct
            );
            ct.ThrowIfCancellationRequested();
            File.Move(
                Path.Combine(outputDirectory, "manifest.partial.json"),
                Path.Combine(outputDirectory, "manifest.json")
            );
            return report;
        }
        catch (Exception exception)
        {
            // The acquisition database is authoritative. Audit recovery is bounded and independent
            // of caller cancellation; an audit failure must not hide the original interruption.
            await SaveInterruption(outputDirectory, commandUuid, exception.GetType().Name);
            throw;
        }
    }

    private static async Task<byte[]> ReadRequestBytes(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read
        );
        if (stream.Length > MaximumRequestBytes)
        {
            throw new InvalidDataException("The acquisition request exceeds 64 KiB.");
        }
        var bytes = new byte[MaximumRequestBytes + 1];
        var read = 0;
        while (read < bytes.Length)
        {
            var count = await stream.ReadAsync(bytes.AsMemory(read), ct);
            if (count == 0)
            {
                break;
            }
            read += count;
        }
        if (read > MaximumRequestBytes)
        {
            throw new InvalidDataException("The acquisition request exceeds 64 KiB.");
        }
        return bytes[..read];
    }

    private static MusicBrainzLookupRequest ParseRequest(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Use a JSON object for the acquisition request.");
            }
            var properties = document.RootElement.EnumerateObject().Select(x => x.Name).ToArray();
            if (
                properties.Distinct(StringComparer.Ordinal).Count() != properties.Length
                || !properties.Contains("kind")
                || !properties.Contains("mbid")
                || !properties.Contains("cacheMode")
            )
            {
                throw new InvalidDataException(
                    "Use one kind, mbid, cacheMode and optional maximumCacheAge."
                );
            }
            var request = JsonSerializer.Deserialize<MusicBrainzLookupRequest>(bytes, Json)!;
            if (
                request.Mbid == Guid.Empty
                || !Enum.IsDefined(request.Kind)
                || !Enum.IsDefined(request.CacheMode)
                || (
                    request.CacheMode == MusicBrainzCacheMode.ReuseFresh
                    && request.MaximumCacheAge is not { Ticks: > 0 }
                )
                || (
                    request.CacheMode == MusicBrainzCacheMode.Refresh
                    && request.MaximumCacheAge != null
                )
            )
            {
                throw new InvalidDataException(
                    "Use a supported MBID and explicit valid cache policy."
                );
            }
            return request;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw new InvalidDataException(
                "Use a valid typed MusicBrainz lookup request.",
                exception
            );
        }
    }

    private static string ClaimOutputDirectory(string output)
    {
        output = Path.GetFullPath(output);
        if (Directory.Exists(output) || File.Exists(output))
        {
            throw new IOException("Choose a new acquisition output directory.");
        }
        Directory.CreateDirectory(output);
        // CreateNew elects one owner even if two invocations race to create the same directory.
        using var claim = new FileStream(
            Path.Combine(output, ".acquisition-owner"),
            FileMode.CreateNew
        );
        return output;
    }

    private async Task ExportAcquisition(
        string output,
        MusicBrainzAcquisition acquisition,
        Dictionary<string, MusicBrainzAcquisitionArtifact> artifacts,
        CancellationToken ct
    )
    {
        await WriteJson(output, "command.json", acquisition, artifacts, ct);
        foreach (var attempt in acquisition.Attempts)
        {
            var prefix = $"attempts/{attempt.Ordinal}-{attempt.AttemptUuid:D}";
            var response = await store.GetResponseAsync(attempt.AttemptUuid, ct);
            if (response != null)
            {
                await WriteJson(
                    output,
                    prefix + "/response.json",
                    response with
                    {
                        Response = response.Response with { Body = null },
                    },
                    artifacts,
                    ct
                );
                if (response.Response.Body != null)
                {
                    await WriteBytes(
                        output,
                        prefix + "/body.bin",
                        response.Response.Body,
                        artifacts,
                        ct
                    );
                }
            }
            var capture = await evidence.GetCaptureAsync(attempt.AttemptUuid, ct);
            if (capture != null)
            {
                await ExportCapture(output, prefix, capture, artifacts, ct);
            }
        }
        // Cached commands have no attempt of their own; retain the referenced original fetch too.
        if (
            acquisition.Result?.FetchUuid is { } fetchUuid
            && !acquisition.Attempts.Any(x => x.AttemptUuid == fetchUuid)
        )
        {
            var capture =
                await evidence.GetCaptureAsync(fetchUuid, ct)
                ?? throw new InvalidDataException("The saved acquisition capture is unavailable.");
            await ExportCapture(output, "cached", capture, artifacts, ct);
        }
    }

    private static async Task ExportCapture(
        string output,
        string prefix,
        NormalizationCaptureResult capture,
        Dictionary<string, MusicBrainzAcquisitionArtifact> artifacts,
        CancellationToken ct
    )
    {
        await WriteBytes(output, prefix + "/input.bin", capture.Revision.Payload, artifacts, ct);
        var decoded = new NormalizationProviderDecoder().Decode(capture.Revision, ct);
        ct.ThrowIfCancellationRequested();
        var decodedBytes = JsonSerializer.SerializeToUtf8Bytes(decoded, Json);
        if (decodedBytes.Length > 32 * 1024 * 1024)
        {
            throw new InvalidDataException("Decoded MusicBrainz observations exceed 32 MiB.");
        }
        await WriteBytes(output, prefix + "/decoded.json", decodedBytes, artifacts, ct);
        await WriteJson(
            output,
            prefix + "/capture.json",
            new
            {
                revision = new
                {
                    capture.Revision.Uuid,
                    capture.Revision.Resource,
                    capture.Revision.Sha256,
                    capture.Revision.MediaType,
                    capture.Revision.CaptureVersion,
                    capture.Revision.CapturedAt,
                },
                capture.Fetch,
            },
            artifacts,
            ct
        );
    }

    private async Task SaveInterruption(string output, Guid commandUuid, string exceptionType)
    {
        using var audit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var acquisition = await service.GetAsync(commandUuid, audit.Token);
            await WriteJson(
                output,
                "interruption.json",
                new
                {
                    commandUuid,
                    exceptionType,
                    acquisition,
                },
                null,
                audit.Token
            );
            if (acquisition != null)
            {
                await ExportAcquisition(
                    Path.Combine(output, "recovery"),
                    acquisition,
                    new(),
                    audit.Token
                );
            }
        }
        catch
        {
            // A partial export can remain. The next invocation reads the durable command again.
        }
    }

    private static async Task WriteJson(
        string output,
        string relative,
        object value,
        Dictionary<string, MusicBrainzAcquisitionArtifact>? artifacts,
        CancellationToken ct
    ) =>
        await WriteBytes(
            output,
            relative,
            JsonSerializer.SerializeToUtf8Bytes(value, Json),
            artifacts,
            ct
        );

    private static async Task WriteBytes(
        string output,
        string relative,
        byte[] bytes,
        Dictionary<string, MusicBrainzAcquisitionArtifact>? artifacts,
        CancellationToken ct
    )
    {
        var path = Path.Combine(output, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read
        );
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
        artifacts?.Add(
            relative,
            new(bytes.LongLength, Convert.ToHexStringLower(SHA256.HashData(bytes)))
        );
    }

    private static async Task<object> AssemblyReceipt(Assembly assembly, CancellationToken ct)
    {
        await using var stream = File.OpenRead(assembly.Location);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return new
        {
            version = assembly.GetName().Version?.ToString(),
            sha256 = Convert.ToHexStringLower(hash),
        };
    }
}

public sealed record MusicBrainzAcquisitionArtifact(long Bytes, string Sha256);

public sealed record MusicBrainzAcquisitionBatchReport(
    string State,
    Guid CommandUuid,
    DateTimeOffset? NotBefore,
    string OutputDirectory
);
