using System.Text.Json;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization.Batch;

public sealed partial class ShadowPerformanceAdapter
{
    private ShadowPerformanceAssociation AssociateRecordingPerformance(
        ShadowCatalogInput input,
        NormalizationInputRevision revision,
        ProviderRecordingObservation recording
    )
    {
        if (!input.Record.TryGetProperty("catalog_binding", out var retained))
        {
            return AssociatePerformance(
                input,
                recording.LiteralDate?.Value,
                recording.Origin,
                recording.LiteralDate,
                recording.Venue,
                recording.Text
            );
        }

        // This binding describes a historical catalog assignment, outside the native
        // provider body. Keep its artifact coordinates separate from provider evidence.
        // An explicit unresolved binding must never choose another recording by date.
        RetainedHttpCatalogBinding? binding = null;
        ShadowPerformanceAssociation Result(string? reason, ShowHome? show = null) =>
            new(
                show?.Uuid,
                show == null ? null : "show:" + show.Uuid.ToString("D"),
                "retained_historical_catalog_source",
                reason,
                recording.LiteralDate?.Value,
                recording.Origin,
                recording.LiteralDate,
                [],
                show == null
                    ? []
                    :
                    [
                        new(
                            show.Uuid,
                            show.Id,
                            show.LiteralDate,
                            show.PerformanceType,
                            show.PerformanceLabel,
                            show.PerformanceKey,
                            show.VenueId,
                            false
                        ),
                    ],
                binding
            );

        try
        {
            binding = retained.Deserialize<RetainedHttpCatalogBinding>(ProviderJson);
            if (binding == null || !RetainedProvenanceMatches(input, revision, binding))
            {
                return Result("invalid_retained_catalog_binding");
            }
        }
        catch (JsonException)
        {
            return Result("invalid_retained_catalog_binding");
        }

        if (binding.Status != "bound")
        {
            return Result(binding.Reason ?? "retained_catalog_source_unresolved");
        }

        var source = BoundCatalogSource(input, binding, recording.Origin.Item);
        if (source == null)
        {
            return Result("retained_catalog_source_conflict");
        }

        var show = BoundCatalogShow(input, binding, source);
        if (show == null)
        {
            return Result(binding.ShowReason ?? "retained_catalog_show_conflict");
        }

        // Updated provider metadata remains visible. It cannot move the source or its
        // playable files away from the captured catalog home.
        var providerDate = ShadowPerformanceDate.Parse(recording.LiteralDate?.Value);
        var catalogDate = ShadowPerformanceDate.Parse(show.LiteralDate);
        var conflict =
            providerDate != null
            && catalogDate != null
            && !providerDate.CompatibleWith(catalogDate);
        return Result(conflict ? "provider_date_conflicts_with_catalog_assignment" : null, show);
    }

    private static bool RetainedProvenanceMatches(
        ShadowCatalogInput input,
        NormalizationInputRevision revision,
        RetainedHttpCatalogBinding binding
    )
    {
        if (!input.Record.TryGetProperty("retained_http", out var retained))
        {
            return true;
        }

        var provenance = retained.Deserialize<RetainedHttpProviderProvenance>(ProviderJson);
        return provenance != null
            && provenance.InputRevisionUuid == revision.Uuid
            && provenance.CatalogBinding == binding
            && provenance.HttpStatus == 200
            && provenance.Body != null
            && provenance.Body.Bytes == revision.Payload.LongLength
            && StringComparer.OrdinalIgnoreCase.Equals(provenance.Body.Sha256, revision.Sha256);
    }

    private SourceHome? BoundCatalogSource(
        ShadowCatalogInput input,
        RetainedHttpCatalogBinding binding,
        NormalizationResourceKey resource
    )
    {
        var context = Object(input.Record, "source_context");
        if (
            input.Artist.Uuid == Guid.Empty
            || input.Artist.Id <= 0
            || Number(input.Row, "artist_id") != input.Artist.Id
            || Uuid(input.Row, "artist_uuid") != input.Artist.Uuid
            || binding.ExpectedSourceUuid == Guid.Empty
            || binding.SourceUuid != binding.ExpectedSourceUuid
            || context == null
            || Uuid(context.Value, "uuid") != binding.SourceUuid
            || Number(context.Value, "artist_id") != input.Artist.Id
            || Number(context.Value, "id") is not { } sourceId
            || conflictingSources.Contains(sourceId)
            || !sources.TryGetValue(sourceId, out var source)
            || source.Uuid != binding.SourceUuid
            || source.ArtistId != input.Artist.Id
            || source.ShowId != Number(context.Value, "show_id")
            || source.UpstreamIdentifier != resource.ProviderKey
            || source.UpstreamIdentifier != Text(context.Value, "upstream_identifier")
        )
        {
            return null;
        }

        return source;
    }

    private ShowHome? BoundCatalogShow(
        ShadowCatalogInput input,
        RetainedHttpCatalogBinding binding,
        SourceHome source
    )
    {
        var context = Object(input.Record, "show_context");
        if (
            binding.ShowUuid == null
            || context == null
            || Uuid(context.Value, "uuid") != binding.ShowUuid
            || Number(context.Value, "artist_id") != input.Artist.Id
            || Number(context.Value, "id") is not { } showId
            || source.ShowId != showId
            || !shows.TryGetValue(showId, out var show)
            || HasConflict(show)
            || show.Uuid != binding.ShowUuid
            || show.ArtistId != input.Artist.Id
        )
        {
            return null;
        }

        return show;
    }
}
