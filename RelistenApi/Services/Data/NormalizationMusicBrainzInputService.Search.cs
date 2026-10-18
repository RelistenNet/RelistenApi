using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationMusicBrainzInputService
{
    public const string SearchVersion =
        "musicbrainz-search-acquisition-v1/" + MusicBrainzCandidateQueryBuilder.Version;

    /// <summary>Drives one bounded search-page GET using the same provider admission and recovery as lookups.</summary>
    public async Task<MusicBrainzSearchAcquisition> AcquireSearchAsync(
        Guid commandUuid,
        MusicBrainzSearchPageRequest request,
        CancellationToken ct = default
    )
    {
        RequireAcquisitionContext(commandUuid, ct);
        var previous = await store.GetCommandAsync(commandUuid, "search", ct);
        if (previous != null)
        {
            if (previous.ToSearch().Request != request)
            {
                throw new NormalizationConflictException(
                    "The command UUID belongs to another MusicBrainz search request."
                );
            }
            return (await ResumeCommandAsync(previous, ct)).ToSearch();
        }
        var descriptor = MusicBrainzCandidateQueryBuilder.CreateDescriptor(request);
        var profile = new MusicBrainzRequestProfile(
            descriptor.Resource,
            descriptor.CaptureVersion,
            descriptor.RequestUrl,
            descriptor
        );
        var command = await store.GetOrCreateCommandAsync(
            commandUuid,
            "search",
            JsonSerializer.Serialize(request),
            profile,
            SearchVersion,
            ct
        );
        return (await ResumeCommandAsync(command, ct)).ToSearch();
    }

    public async Task<MusicBrainzSearchAcquisition> ResumeSearchAsync(
        Guid commandUuid,
        CancellationToken ct = default
    )
    {
        RequireAcquisitionContext(commandUuid, ct);
        var command =
            await store.GetCommandAsync(commandUuid, "search", ct)
            ?? throw new ArgumentException(
                "The MusicBrainz search command does not exist.",
                nameof(commandUuid)
            );
        return (await ResumeCommandAsync(command, ct)).ToSearch();
    }

    public Task<MusicBrainzSearchAcquisition?> GetSearchAsync(
        Guid commandUuid,
        CancellationToken ct = default
    ) => store.GetSearchAsync(commandUuid, ct);

    private static string? ValidateSearchEnvelope(
        JsonElement root,
        MusicBrainzSearchDescriptor descriptor
    )
    {
        var array = descriptor.Kind switch
        {
            MusicBrainzLookupKind.Work => "works",
            MusicBrainzLookupKind.Recording => "recordings",
            MusicBrainzLookupKind.Artist => "artists",
            _ => throw new ArgumentException("Unsupported search profile."),
        };
        if (
            !root.TryGetProperty("count", out var count)
            || count.ValueKind != JsonValueKind.Number
            || !count.TryGetInt64(out var total)
            || total < 0
            || !root.TryGetProperty("offset", out var offset)
            || offset.ValueKind != JsonValueKind.Number
            || !offset.TryGetInt32(out var start)
            || start != descriptor.Offset
            || !root.TryGetProperty(array, out var hits)
            || hits.ValueKind != JsonValueKind.Array
            || hits.GetArrayLength() > descriptor.Limit
        )
        {
            return "invalid_search_envelope";
        }
        return null;
    }
}
