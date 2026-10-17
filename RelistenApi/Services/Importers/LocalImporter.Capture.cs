using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hangfire.Server;
using Newtonsoft.Json;
using Relisten.Api.Models;
using Relisten.Api.Models.Normalization;
using Relisten.Vendor.Local;

namespace Relisten.Import;

// Local roots select dates; directory responses contain the original notes and file tags.
// Retain each response before the catalog combines directories into one date's recording.
public partial class LocalImporter
{
    private const string LocalCaptureVersion = "local-http-json-v1";

    // Local identifiers name a metadata root, rather than an optional provider artist ID.
    private static string RequireMetadataRoot(ArtistUpstreamSource source) =>
        !string.IsNullOrWhiteSpace(source.upstream_identifier)
            ? source.upstream_identifier
            : throw new InvalidDataException(
                "Local provider mapping is missing its metadata root."
            );

    private Task<LocalRootObject<LocalShow>> LocalRootRequest(
        string metadataRoot,
        PerformContext? context,
        CancellationToken ct = default
    ) =>
        FetchLocalResponse<LocalRootObject<LocalShow>>(
            new("local", "metadata_root", metadataRoot),
            $"{metadataRoot}/metadata",
            context,
            IsValidRoot,
            ct
        );

    private Task<LocalShowObject> LocalDirectoryRequest(
        string metadataRoot,
        string directory,
        PerformContext? context,
        CancellationToken ct = default
    ) =>
        FetchLocalResponse<LocalShowObject>(
            // A tuple keeps root/directory boundaries distinct even when either contains '/'.
            new(
                "local",
                "directory_metadata",
                JsonConvert.SerializeObject(new[] { metadataRoot, directory })
            ),
            $"{metadataRoot}/{directory}/metadata",
            context,
            IsValidDirectory,
            ct
        );

    private static bool IsValidRoot(LocalRootObject<LocalShow> value) =>
        value?.data != null
        && value.data.All(day =>
            day.Value != null
            && day.Value.All(show =>
                show != null
                && show.date == day.Key
                && !string.IsNullOrWhiteSpace(show.dir)
                && show.venue != null
            )
        );

    private static bool IsValidDirectory(LocalShowObject value) =>
        value?.tracks != null
        && value.txts != null
        && value.txts.All(text => text != null)
        && value.tracks.All(track =>
            track != null
            && !string.IsNullOrWhiteSpace(track.mp3)
            && track.tags?.title != null
            && track.tags.track != null
        );

    private Task<T> FetchLocalResponse<T>(
        NormalizationResourceKey resource,
        string route,
        PerformContext? context,
        System.Func<T, bool> valid,
        CancellationToken ct
    ) =>
        normalizationInputs.FetchAsync(
            resource,
            LocalCaptureVersion,
            async token =>
            {
                var url = LocalApiUrl(route);
                ImportProgress.WriteLine(log, context, $"Requesting {url}");
                using var response = await http.GetAsync(url, token);
                response.EnsureSuccessStatusCode();
                var bytes = await response.Content.ReadAsByteArrayAsync(token);
                var value = JsonConvert.DeserializeObject<T>(
                    await response.Content.ReadAsStringAsync(token)
                )!;
                if (normalizationInputs.Enabled && !valid(value))
                {
                    throw new InvalidDataException(
                        "Local metadata is missing required catalog fields."
                    );
                }

                return new NormalizationProviderResponse<T>(
                    value,
                    bytes,
                    response.Content.Headers.ContentType?.ToString() ?? "application/json",
                    response.Headers.ETag?.ToString()
                        ?? response.Content.Headers.LastModified?.ToString("O")
                );
            },
            ct
        );

    private async Task<Dictionary<string, LocalShowObject>> FetchDirectoryMetadataAsync(
        string metadataRoot,
        IEnumerable<LocalShow> shows,
        PerformContext? context,
        CancellationToken ct = default
    )
    {
        var metadata = new Dictionary<string, LocalShowObject>();
        foreach (var directory in shows.Select(show => show.dir).Distinct())
        {
            metadata[directory] =
                await LocalDirectoryRequest(metadataRoot, directory, context, ct)
                ?? throw new InvalidDataException("Local directory metadata is missing.");
        }

        return metadata;
    }
}
