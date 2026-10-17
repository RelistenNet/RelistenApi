using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace Relisten.Data;

// Imported row UUIDs establish existing ownership, not normalized identities. These checked
// paths keep retained wording and its original producer revisions in the same factual read set.
public sealed partial class NormalizationCatalogBindingService
{
    /// <summary>
    /// Decodes a retained imported venue and verifies its original facts and revisions against
    /// one current primary snapshot. Returns the existing venue UUID, including artistless venues.
    /// A conflict requires a complete refresh. No physical-venue match is accepted.
    /// </summary>
    /// <remarks>
    /// Pass Inputs, CatalogInputs and ImportedVenues to work admission. The receipt proves a
    /// completed capture; admission separately checks whether that input remains current.
    /// </remarks>
    public Task<NormalizationImportedVenueBindingSnapshot> BindRefreshedImportedVenueAsync(
        NormalizationImportedVenueSnapshot snapshot,
        CancellationToken cancellationToken = default
    )
    {
        var prepared = PrepareImportedSnapshot(
            snapshot.Capture,
            "imported_venue",
            NormalizationImportedCatalogService.ImportedVenueCaptureVersion,
            cancellationToken
        );
        var catalogInputs = snapshot.CatalogInputs.ToArray();
        var importedVenues = snapshot.ImportedVenues.ToArray();
        return WithCatalogSnapshotAsync(
            async (c, t, ct) =>
            {
                await RequireRefreshReceiptAsync(c, t, prepared.Capture, prepared.PayloadHash, ct);
                var current = await NormalizationImportedCatalogService.ReadVenueProjectionAsync(
                    c,
                    t,
                    prepared.ResourceUuid,
                    ct
                );
                if (
                    current == null
                    || !SameImportedPayload(prepared.Payload, current.Payload)
                    || !catalogInputs
                        .OrderBy(x => x.ArtistUuid)
                        .SequenceEqual(current.CatalogInputs.OrderBy(x => x.ArtistUuid))
                    || !importedVenues
                        .OrderBy(x => x.VenueUuid)
                        .SequenceEqual(current.ImportedVenues.OrderBy(x => x.VenueUuid))
                )
                {
                    throw ImportedRefreshConflict();
                }

                return new NormalizationImportedVenueBindingSnapshot(
                    prepared.Capture.Revision.Uuid,
                    prepared.Capture.Revision.Resource,
                    new(
                        NormalizationCatalogBindingStatus.Bound,
                        new(
                            prepared.ResourceUuid,
                            current.CatalogInputs.SingleOrDefault()?.ArtistUuid
                        ),
                        "imported_venue_uuid"
                    ),
                    current.CatalogInputs,
                    current.ImportedVenues
                );
            },
            cancellationToken
        );
    }

    /// <summary>
    /// Decodes and binds a complete retained artist dictionary by stored song UUID. Missing
    /// titles remain missing. An existing artist with no songs returns an empty dictionary.
    /// A conflict requires refreshing the entire dictionary, not attaching newer clocks.
    /// </summary>
    /// <remarks>
    /// Pass Inputs and CatalogInputs to work admission. These are imported repertoire rows;
    /// binding does not accept song identity, composition identity, aliases or authorship.
    /// </remarks>
    public Task<NormalizationRepertoireBindingSnapshot> BindRefreshedRepertoireAsync(
        NormalizationArtistRepertoireSnapshot snapshot,
        CancellationToken cancellationToken = default
    )
    {
        var prepared = PrepareImportedSnapshot(
            snapshot.Capture,
            "artist_repertoire",
            NormalizationImportedCatalogService.RepertoireCaptureVersion,
            cancellationToken
        );
        var catalogInputs = snapshot.CatalogInputs.ToArray();
        return WithCatalogSnapshotAsync(
            async (c, t, ct) =>
            {
                await RequireRefreshReceiptAsync(c, t, prepared.Capture, prepared.PayloadHash, ct);
                var current =
                    await NormalizationImportedCatalogService.ReadRepertoireProjectionAsync(
                        c,
                        t,
                        prepared.ResourceUuid,
                        ct
                    );
                if (
                    current == null
                    || !SameImportedPayload(prepared.Payload, current.Payload)
                    || !catalogInputs
                        .OrderBy(x => x.ArtistUuid)
                        .SequenceEqual(current.CatalogInputs.OrderBy(x => x.ArtistUuid))
                )
                {
                    throw ImportedRefreshConflict();
                }

                var songs = prepared
                    .Observations.Repertoire.Select(song => new NormalizationImportedSongBinding(
                        song,
                        new(
                            NormalizationCatalogBindingStatus.Bound,
                            new(prepared.ResourceUuid, Guid.Parse(song.Origin.Item.ProviderKey)),
                            "imported_song_uuid"
                        )
                    ))
                    .ToArray();
                return new NormalizationRepertoireBindingSnapshot(
                    prepared.Capture.Revision.Uuid,
                    prepared.Capture.Revision.Resource,
                    new(
                        NormalizationCatalogBindingStatus.Bound,
                        new(prepared.ResourceUuid),
                        "imported_artist_uuid"
                    ),
                    songs,
                    current.CatalogInputs
                );
            },
            cancellationToken
        );
    }

    private static PreparedImportedSnapshot PrepareImportedSnapshot(
        NormalizationCaptureResult capture,
        string resourceType,
        string captureVersion,
        CancellationToken ct
    )
    {
        ct.ThrowIfCancellationRequested();
        var input = capture.Revision;
        if (
            input.Resource.Provider != "legacy_catalog"
            || input.Resource.ResourceType != resourceType
            || input.CaptureVersion != captureVersion
            || !Guid.TryParseExact(input.Resource.ProviderKey, "D", out var resourceUuid)
            || resourceUuid == Guid.Empty
        )
        {
            throw new ArgumentException(
                "Checked imported binding requires its supported catalog snapshot."
            );
        }
        if (!capture.Fetch.AcceptedAsCurrent || capture.Fetch.InputRevisionUuid != input.Uuid)
        {
            throw ImportedRefreshConflict();
        }

        // Freeze caller-owned bytes before any await. Decoding and receipt validation must use
        // the same payload, even when the caller keeps and modifies its original array.
        var payload = input.Payload.ToArray();
        var hash = Convert.ToHexStringLower(SHA256.HashData(payload));
        var frozen = new NormalizationInputRevision
        {
            Uuid = input.Uuid,
            Resource = input.Resource,
            Payload = payload,
            Sha256 = hash,
            MediaType = input.MediaType,
            CaptureVersion = input.CaptureVersion,
            CapturedAt = input.CapturedAt,
        };
        var observations = new NormalizationProviderDecoder().Decode(frozen, ct);
        using var document = JsonDocument.Parse(payload);
        return new(
            new(frozen, capture.Fetch),
            resourceUuid,
            hash,
            document.RootElement.Clone(),
            observations
        );
    }

    private static bool SameImportedPayload(JsonElement retained, string current)
    {
        using var document = JsonDocument.Parse(current);
        return JsonElement.DeepEquals(retained, document.RootElement);
    }

    private static NormalizationConflictException ImportedRefreshConflict() =>
        new(
            "Imported catalog facts or their producer revisions changed. Refresh the complete venue or artist dictionary before binding."
        );

    private sealed record PreparedImportedSnapshot(
        NormalizationCaptureResult Capture,
        Guid ResourceUuid,
        string PayloadHash,
        JsonElement Payload,
        ProviderDecodeResult Observations
    );
}
