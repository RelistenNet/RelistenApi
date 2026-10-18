using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationMusicBrainzInputStore
{
    internal async Task<MusicBrainzSearchAcquisition> GetOrCreateSearchAsync(
        Guid commandUuid,
        MusicBrainzSearchPageRequest request,
        MusicBrainzSearchDescriptor descriptor,
        string acquisitionVersion,
        CancellationToken ct = default
    ) =>
        (
            await GetOrCreateCommandAsync(
                commandUuid,
                "search",
                JsonSerializer.Serialize(request),
                new(
                    descriptor.Resource,
                    descriptor.CaptureVersion,
                    descriptor.RequestUrl,
                    descriptor
                ),
                acquisitionVersion,
                ct
            )
        ).ToSearch();

    public async Task<MusicBrainzSearchAcquisition?> GetSearchAsync(
        Guid commandUuid,
        CancellationToken ct = default
    ) => (await GetCommandAsync(commandUuid, "search", ct))?.ToSearch();

    internal async Task<MusicBrainzSearchAcquisition> FailSearchAsync(
        Guid commandUuid,
        string reason,
        CancellationToken ct = default
    ) => (await FailCommandAsync(commandUuid, reason, ct, "search")).ToSearch();
}
